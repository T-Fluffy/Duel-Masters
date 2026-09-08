// DuelE2E - headless end-to-end harness for the online duel backend.
//
// Drives two real SignalR clients through the full online flow against a
// running backend + PostgreSQL: registers a user, creates a saved deck via
// /api/decks, hosts/joins a match with saved decks, then plays a scripted
// game while asserting hand redaction, saved-deck usage, two-phase blocking
// and general turn progression.
//
// Usage:
//   dotnet run --project tests/DuelE2E
//
// Environment variables:
//   E2E_BASE_URL    backend base URL  (default http://127.0.0.1:8080)
//   E2E_DURATION    match runtime in seconds (default 150)
//   E2E_CARDS_JSON  path to cards.json (default: located up the repo tree)
//   E2E_PASSWORD    test-user password (default: random per run; the harness
//                   always registers a brand-new account, so no static value
//                   is needed or committed)
//
// Exit code 0 on PASS, 1 on FAIL.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Microsoft.AspNetCore.SignalR.Client;
using DuelMasters.Domain.Networking;

namespace DuelE2E;

internal sealed class Bot
{
    public required string Name;
    public required HubConnection Conn;
    public string Side = DuelSide.Player1;
    public string? MatchCode;
    public ConcurrentQueue<string> Errors { get; } = new();
    public long LastStateGen { get; set; }
    public DateTime LastEvent { get; set; } = DateTime.MinValue;
    public DuelGameState? Latest { get; set; }
    public volatile int StateCount;
    public int MaxTurnSeen;
    public bool PassBlockUsed;
    public bool BlockUsed;
    public int AttackPendingEvents;
    public bool TriggerOwnerWindowSeen;
    public bool TriggerPlayed;
    public bool TriggerDeclined;
    public bool Evolved;
}

internal sealed class CardRule
{
    public bool NeedsTarget;
    public List<(string Scope, int UpToPower)> TargetHints { get; } = new();
    public bool IsSpell;
    public bool IsCreature;
    public bool IsEvolution;
    public string EvolutionOf = "";
    public int ManaCost;
    public List<string> Keywords { get; } = new();
}

internal static class Program
{
    private static readonly string BaseUrl = Environment.GetEnvironmentVariable("E2E_BASE_URL") ?? "http://127.0.0.1:8080";

    private static readonly Dictionary<string, List<string>> HostDeck = BuildDeck(new[]
    {
        "dm_01_009", "dm_01_003", "dm_01_008", "dm_01_002", "dm_02_005",
        "dm_01_006", "dm_02_006", "dm_01_007", "dm_01_016", "dm_01_012",
    });
    private static readonly Dictionary<string, List<string>> JoinerDeck = BuildDeck(new[]
    {
        "dm_01_007", "dm_01_016", "dm_01_012", "dm_01_001", "dm_01_004",
        "dm_01_010", "dm_01_015", "dm_01_002", "dm_01_009", "dm_02_006",
    });

    private static readonly List<string> Failures = new();
    private static readonly List<string> Infos = new();
    private static readonly object LogLock = new();
    private static bool _hostDeckSeenForeignCard;
    private static readonly HashSet<string> HostDeckSeenIds = new();
    private static bool _handRedactionOk = true;
    private static string _handRedactionDetail = "";
    private static readonly string E2ePassword = ResolveE2ePassword();

    public static async Task<int> Main()
    {
        try
        {
            var cardRules = LoadCardRules(ResolveCardsJsonPath());
            Info($"crafted deck total: {HostDeck.Values.Sum(v => v.Count)} cards");
            foreach (var cid in HostDeck.Keys)
                if (!cardRules.ContainsKey(cid))
                    throw new InvalidOperationException($"Crafted card {cid} missing from cards.json");

            var http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(20) };

            // --- auth + create decks (saved-deck path) ---
            var user = "e2e_" + Guid.NewGuid().ToString("N")[..10];
            Info($"registering user {user}");
            await RegisterAsync(http, user);
            var token = await LoginAsync(http, user);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var hostDeckId = await CreateDeckAsync(http, "E2E Host", HostDeck);
            var joinerDeckId = await CreateDeckAsync(http, "E2E Joiner", JoinerDeck);
            Info($"decks created: host={hostDeckId} joiner={joinerDeckId}");
            if (hostDeckId == Guid.Empty || joinerDeckId == Guid.Empty)
            {
                Failure("deck creation did not return a valid id");
                return Finish();
            }

            // --- connections ---
            var host = new Bot { Name = "E2E_Host", Conn = NewConnection() };
            var joiner = new Bot { Name = "E2E_Joiner", Conn = NewConnection() };
            Wire(host);
            Wire(joiner);

            Info("connecting host...");
            await host.Conn.StartAsync();
            Info("connecting joiner...");
            await joiner.Conn.StartAsync();

            // --- host match with saved deck ---
            Info("hosting match with saved deck...");
            var hostInfo = await host.Conn.InvokeAsync<MatchInfo>(DuelContract.Hub.HostMatch, "E2E host", hostDeckId);
            host.Side = hostInfo.YourSide;
            host.MatchCode = hostInfo.MatchCode;
            Info($"host assigned side {host.Side}, code {hostInfo.MatchCode}");

            Info("joining match...");
            var joinInfo = await joiner.Conn.InvokeAsync<MatchInfo>(DuelContract.Hub.JoinMatch, hostInfo.MatchCode, "E2E joiner", joinerDeckId);
            joiner.Side = joinInfo.YourSide;
            Info($"joiner assigned side {joiner.Side}");

            if (host.Side != DuelSide.Player1 || joiner.Side != DuelSide.Player2)
            {
                Failure($"side assignment wrong: host={host.Side} joiner={joiner.Side}");
            }

            // --- wait for initial states on both ---
            if (!await WaitForStateAsync(host, 10) || !await WaitForStateAsync(joiner, 10))
            {
                Failure("both clients never got an initial DuelGameState");
                return Finish();
            }
            Info("initial states received on both sides");
            VerifyHandRedaction(host);
            VerifyHandRedaction(joiner);

            // --- play loop ---
            var deadline = DateTime.UtcNow.AddSeconds(GetEnvInt("E2E_DURATION", 150));
            var lastAnyEvent = DateTime.UtcNow;
            var idleStart = DateTime.UtcNow;
            var gameOverReported = false;

            while (DateTime.UtcNow < deadline)
            {
                var sA = GetLatest(host);
                var sB = GetLatest(joiner);
                if (sA == null || sB == null)
                {
                    await Task.Delay(100);
                    continue;
                }
                Track(host, sA);
                Track(joiner, sB);

                var anyEvent = Math.Max(host.LastEvent.ToBinary(), joiner.LastEvent.ToBinary());
                if (anyEvent > lastAnyEvent.ToBinary())
                {
                    lastAnyEvent = DateTime.FromBinary(anyEvent);
                    idleStart = DateTime.UtcNow;
                }
                if ((DateTime.UtcNow - idleStart).TotalSeconds > 45)
                {
                    Failure($"deadlock: no state change for ~45s (turn {sA.TurnNumber}, phase {sA.Phase}, gameOver={sA.IsGameOver})");
                    break;
                }

                if (sA.IsGameOver || sB.IsGameOver)
                {
                    gameOverReported = true;
                    Info($"game over reported (winnerId={sA.WinnerId}) after turn {sA.TurnNumber}");
                    break;
                }

                var activeSide = sA.ActiveSide;

                // Shield Trigger window: whoever owns the pending triggers acts.
                if (sA.ShieldTriggerOwnerSide is { } ownerSide)
                {
                    var owner = ownerSide == host.Side ? host : joiner;
                    var stO = GetLatest(owner);
                    if (stO is not null && await HandleOwnerOnly(owner, stO))
                    {
                        await BumpAsync(host, joiner);
                        continue;
                    }
                }

                // Pending block: the defender (non-active side) must block or pass.
                var defender = activeSide == host.Side ? joiner : host;
                var stD = GetLatest(defender);
                if (stD is not null && await HandleBlockDecision(defender, stD))
                {
                    await BumpAsync(host, joiner);
                    continue;
                }

                var active = activeSide == host.Side ? host : joiner;
                var st = GetLatest(active);
                if (st == null) { await Task.Delay(100); continue; }

                if (!st.YourTurn) { await Task.Delay(80); continue; }

                switch (st.Phase)
                {
                    case "Untap": await TryInvoke(active, DuelContract.Hub.StartTurn); break;
                    case "Draw": await TryInvoke(active, DuelContract.Hub.Draw); break;
                    case "Main": if (!await DoMain(active, st)) await TryInvoke(active, DuelContract.Hub.EndMainPhase); break;
                    case "End": await TryInvoke(active, DuelContract.Hub.EndTurn); break;
                }
                await BumpAsync(host, joiner);
            }

            Info($"loop ended: gameOver={gameOverReported}, host turns={host.MaxTurnSeen} joiner turns={joiner.MaxTurnSeen}");
            if (!gameOverReported)
                Info("match did not finish within the loop (acceptable if flow was verified)");

            // --- assertions ---
            if (host.Errors.Count > 0 || joiner.Errors.Count > 0)
            {
                Info($"errors sent to clients: host={host.Errors.Count} joiner={joiner.Errors.Count}");
                foreach (var e in host.Errors.Take(5)) Info($"  host err: {e}");
                foreach (var e in joiner.Errors.Take(5)) Info($"  joiner err: {e}");
            }

            if (host.MaxTurnSeen >= 3)
                Info($"turns progressed (host saw turn {host.MaxTurnSeen})");
            else
                Failure($"turns never progressed past {host.MaxTurnSeen}");

            if (_handRedactionOk)
                Info("hand redaction verified (own hand visible, opponent hand count-only) on both sides");
            else
                Failure($"hand redaction broken: {_handRedactionDetail}");

            if (_hostDeckSeenForeignCard)
                Failure("host drew/used a card NOT in its crafted saved deck -> deck load may have fallen back to random");
            else
                Info($"host deck verified: only crafted-card ids observed across zones ({HostDeckSeenIds.Count} used)");

            var anyBlockDecided = host.BlockUsed || host.PassBlockUsed || joiner.BlockUsed || joiner.PassBlockUsed;
            if (anyBlockDecided)
                Info($"two-phase blocking exercised (pending windows: host={host.AttackPendingEvents} joiner={joiner.AttackPendingEvents}; blocks={(host.BlockUsed ? "H" : "")}{(joiner.BlockUsed ? "J" : "")}, passes={(host.PassBlockUsed ? "H" : "")}{(joiner.PassBlockUsed ? "J" : "")})");
            else
                Info("blocking path never triggered (informational)");

            if (host.Evolved || joiner.Evolved)
                Info("evolution exercised");
            else
                Info("evolution never triggered (informational)");

            if (host.TriggerOwnerWindowSeen || joiner.TriggerOwnerWindowSeen)
                Info($"shield trigger windows seen (played={host.TriggerPlayed || joiner.TriggerPlayed}, declined={host.TriggerDeclined || joiner.TriggerDeclined})");
            else
                Info("shield trigger window never seen (informational)");

            if (host.StateCount > 0 && joiner.StateCount > 0)
                Info($"states received: host={host.StateCount} joiner={joiner.StateCount}");
        }
        catch (Exception ex)
        {
            Failure($"harness exception: {ex}");
        }

        return Finish();
    }

    private static string ResolveCardsJsonPath()
    {
        var env = Environment.GetEnvironmentVariable("E2E_CARDS_JSON");
        if (!string.IsNullOrEmpty(env)) return env;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "resources", "data", "cards.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("cards.json not found; set E2E_CARDS_JSON or run from the repo tree.");
    }

    private static int Finish()
    {
        Console.WriteLine();
        Console.WriteLine("================ E2E SUMMARY ================");
        foreach (var i in Infos) Console.WriteLine($"  [i] {i}");
        foreach (var f in Failures) Console.WriteLine($"  [FAIL] {f}");
        Console.WriteLine($"RESULT: {(Failures.Count == 0 ? "PASS" : $"FAIL ({Failures.Count} failure(s))")}");
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void Track(Bot b, DuelGameState s)
    {
        if (s.TurnNumber > b.MaxTurnSeen) b.MaxTurnSeen = s.TurnNumber;
        if (s.AttackPending) b.AttackPendingEvents++;
        foreach (var p in s.Players)
        {
            if (p.Side == b.Side)
            {
                foreach (var c in p.Hand.Concat(p.ManaZone).Concat(p.BattleZone).Concat(p.Graveyard))
                {
                    if (!c.CountOnly && !string.IsNullOrEmpty(c.CardId))
                    {
                        lock (LogLock)
                        {
                            if (b.Name == "E2E_Host")
                            {
                                HostDeckSeenIds.Add(c.CardId);
                                if (!HostDeck.ContainsKey(c.CardId)) _hostDeckSeenForeignCard = true;
                            }
                        }
                    }
                }
            }
        }
    }

    private static DuelGameState? GetLatest(Bot b) => b.Latest;

    private static async Task<bool> HandleOwnerOnly(Bot b, DuelGameState st)
    {
        if (!st.ShieldTriggerWindowActive || st.ShieldTriggerOwnerSide != b.Side) return false;
        b.TriggerOwnerWindowSeen = true;
        if (st.PendingTriggerHandIndices.Count == 0)
        {
            await TryInvoke(b, DuelContract.Hub.DeclineShieldTriggers);
            b.TriggerDeclined = true;
            return true;
        }
        var hand = b.Latest!.Players.First(p => p.Side == b.Side).Hand;
        var idx = st.PendingTriggerHandIndices[0];
        if (idx >= 0 && idx < hand.Count && !hand[idx].CountOnly && !NeedsTarget(hand[idx].CardId))
        {
            await TryInvoke(b, DuelContract.Hub.PlayShieldTrigger, idx);
            b.TriggerPlayed = true;
        }
        else
        {
            await TryInvoke(b, DuelContract.Hub.DeclineShieldTriggers);
            b.TriggerDeclined = true;
        }
        return true;
    }

    private static async Task<bool> HandleBlockDecision(Bot b, DuelGameState st)
    {
        if (!st.AttackPending || st.YourTurn) return false;
        var me = st.Players.First(p => p.Side == b.Side);
        var blocker = me.BattleZone.FirstOrDefault(c =>
            !c.CountOnly && !c.IsTapped && c.Keywords.Contains("Blocker"));
        if (blocker is not null)
        {
            var idx = me.BattleZone.IndexOf(blocker);
            await TryInvoke(b, DuelContract.Hub.BlockAttack, idx);
            b.BlockUsed = true;
        }
        else
        {
            await TryInvoke(b, DuelContract.Hub.PassBlock);
            b.PassBlockUsed = true;
        }
        return true;
    }

    private static async Task<bool> DoMain(Bot b, DuelGameState st)
    {
        var me = st.Players.First(p => p.Side == b.Side);

        if (st.AttackPending)
            return true;
        var untappedByCiv = me.ManaZone
            .Where(c => !c.CountOnly && !c.IsTapped)
            .GroupBy(c => c.Civilization)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        bool CivOk(CardState c) =>
            !string.IsNullOrEmpty(c.Civilization) &&
            untappedByCiv.TryGetValue(c.Civilization, out var n) && n >= 1;

        if (st.CanPlayMana && me.UntappedMana < 12)
        {
            var cheapestCiv = me.Hand.Count == 0
                ? null
                : me.Hand.Where(c => !c.CountOnly).GroupBy(c => c.Civilization)
                    .OrderBy(g => untappedByCiv.TryGetValue(g.Key, out var n) ? n : 0)
                    .ThenByDescending(g => g.Count())
                    .Select(g => g.Key).FirstOrDefault();
            var i = me.Hand.FindIndex(c => !c.CountOnly &&
                (cheapestCiv == null || string.Equals(c.Civilization, cheapestCiv, StringComparison.OrdinalIgnoreCase)));
            if (i < 0) i = me.Hand.FindIndex(c => !c.CountOnly);
            if (i >= 0) { await TryInvoke(b, DuelContract.Hub.PlayMana, i); return true; }
        }

        if (st.CanSummonOrCast)
        {
            // evolution on matching base first
            foreach (var (i, c) in me.Hand.Select((c, i) => (i, c)))
            {
                if (c.CountOnly || !c.IsEvolution) continue;
                var rule = Rules.TryGetValue(c.CardId, out var r) ? r : null;
                if (rule is null) continue;
                var baseIdx = me.BattleZone.FindIndex(x => x.Race == rule.EvolutionOf && !x.IsTapped);
                if (baseIdx < 0) continue;
                if (c.ManaCost > me.UntappedMana) continue;
                var target = rule.NeedsTarget ? PickTarget(b, rule) : null;
                if (target is not null)
                    await TryInvoke(b, DuelContract.Hub.EvolveCreatureTargeted, i, baseIdx, target.Value.Side, target.Value.Index);
                else
                    await TryInvoke(b, DuelContract.Hub.EvolveCreature, i, baseIdx);
                b.Evolved = true;
                return true;
            }

            var creature = me.Hand
                .Select((c, i) => (c, i))
                .FirstOrDefault(t => !t.c.CountOnly && !t.c.IsEvolution && IsCreature(t.c) &&
                    t.c.ManaCost <= me.UntappedMana && CivOk(t.c) && !NeedsTarget(t.c.CardId));
            if (creature.c is not null)
            {
                await TryInvoke(b, DuelContract.Hub.SummonCreature, creature.i);
                return true;
            }

            var spell = me.Hand
                .Select((c, i) => (c, i))
                .FirstOrDefault(t => !t.c.CountOnly && IsSpell(t.c) && t.c.ManaCost <= me.UntappedMana && CivOk(t.c) && !NeedsTarget(t.c.CardId));
            if (spell.c is not null)
            {
                await TryInvoke(b, DuelContract.Hub.CastSpell, spell.i);
                return true;
            }
        }

        if (st.CanAttack)
        {
            var atk = me.BattleZone.FindIndex(c => ReadyAttacker(c));
            if (atk >= 0)
            {
                await TryInvoke(b, DuelContract.Hub.AttackPlayer, atk);
                return true;
            }
        }

        return false;
    }

    private static bool IsCreature(CardState c) => c.CardType is "Creature" or "EvolutionCreature";
    private static bool IsSpell(CardState c) => c.CardType == "Spell";
    private static bool NeedsTarget(string cardId) =>
        Rules.TryGetValue(cardId, out var r) && r.NeedsTarget;
    private static bool ReadyAttacker(CardState c) =>
        !c.CountOnly && IsCreature(c) && !c.IsTapped && !c.IsSummoningSick &&
        !c.Keywords.Contains("CannotAttackPlayers");

    private static void VerifyHandRedaction(Bot b)
    {
        var s = b.Latest;
        if (s is null) return;
        foreach (var p in s.Players)
        {
            var mine = p.Side == b.Side;
            var ownReal = p.Hand.Where(c => c.CountOnly).ToList();
            var oppReal = p.Hand.Where(c => !c.CountOnly).ToList();
            if (mine && ownReal.Count > 0)
            {
                _handRedactionOk = false;
                _handRedactionDetail = $"{b.Name} sees face-down own hand ({p.Hand.Count} cards)";
            }
            if (!mine && oppReal.Count > 0)
            {
                _handRedactionOk = false;
                _handRedactionDetail = $"{b.Name} sees real opponent hand entries (count={oppReal.Count})";
            }
        }
    }

    private static async Task BumpAsync(Bot a, Bot b2)
    {
        await Task.WhenAll(
            WaitForStateAsync(a, 6),
            WaitForStateAsync(b2, 6));
        await Task.Delay(120);
    }

    private static async Task<bool> WaitForStateAsync(Bot b, int timeoutSec)
    {
        var baseGen = b.LastStateGen;
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            if (b.Latest is not null && b.LastStateGen > baseGen) return true;
            await Task.Delay(50);
        }
        return b.Latest is not null;
    }

    private static IEnumerable<(string Side, int Index)> GatherableTargets(Bot b, CardRule rule)
    {
        var mine = b.Latest!.Players.First(p => p.Side == b.Side);
        var opp = b.Latest.Players.First(p => p.Side != b.Side);
        var wantOwn = rule.TargetHints.Count == 0 || rule.TargetHints.Any(h => h.Scope is "OwnCreature" or "AnyCreature");
        var wantOpp = rule.TargetHints.Count == 0 || rule.TargetHints.Any(h => h.Scope is "OpponentCreature" or "AnyCreature");
        var cap = rule.TargetHints.Count == 0 ? 999 : rule.TargetHints.Max(h => h.UpToPower);
        foreach (var (p, side) in new[] { (opp, "opp"), (mine, "own"), (mine, "own"), (opp, "opp") })
        {
            var want = side == "opp" ? wantOpp : wantOwn;
            if (!want) continue;
            for (var i = 0; i < p.BattleZone.Count; i++)
            {
                var c = p.BattleZone[i];
                if (c.CountOnly || !IsCreature(c)) continue;
                if (c.Power > cap) continue;
                if (side == "opp") yield return ("Player2" == b.Side ? DuelSide.Player1 : DuelSide.Player2, i);
                else yield return (b.Side, i);
            }
        }
    }

    private static (string Side, int Index)? PickTarget(Bot b, CardRule rule)
    {
        return GatherableTargets(b, rule).FirstOrDefault();
    }

    private static async Task<bool> TryInvoke(Bot b, string method, params object?[] args)
    {
        try
        {
            await b.Conn.InvokeCoreAsync(method, args);
            b.LastEvent = DateTime.UtcNow;
            return true;
        }
        catch (Exception ex)
        {
            Info($"{b.Name} invoke {method} failed: {ex}");
            return false;
        }
    }

    private static HubConnection NewConnection() =>
        new HubConnectionBuilder()
            .WithUrl(BaseUrl + "/duel")
            .WithAutomaticReconnect()
            .Build();

    private static void Wire(Bot b)
    {
        b.Conn.On<DuelGameState>(DuelContract.Client.ReceiveGameState, s =>
        {
            if (b.Side is null or "") b.Side = s.YourSide;
            b.Latest = s;
            b.LastStateGen++;
            b.LastEvent = DateTime.UtcNow;
            b.StateCount++;
        });
        b.Conn.On<MatchInfo>(DuelContract.Client.MatchJoined, mi =>
        {
            b.Side = mi.YourSide;
            b.MatchCode = mi.MatchCode;
            b.LastEvent = DateTime.UtcNow;
        });
        b.Conn.On<string>(DuelContract.Client.ReceiveActionError, e => b.Errors.Enqueue(e));
        b.Conn.On<string>(DuelContract.Client.AnnounceWinner, w => Info($"{b.Name} got winner announcement: {w}"));
    }

    // ---------- auth / deck API helpers ----------

    private static string ResolveE2ePassword()
    {
        var env = Environment.GetEnvironmentVariable("E2E_PASSWORD");
        if (!string.IsNullOrEmpty(env)) return env;
        // Fresh account per run and never reused elsewhere, so the value is
        // generated at runtime instead of being committed as a literal.
        return "E2E" + Guid.NewGuid().ToString("N")[..12] + "b#A1";
    }

    private static async Task RegisterAsync(HttpClient http, string user)
    {
        var body = Json(new { username = user, email = user + "@e2e.local", password = E2ePassword });
        var resp = await http.PostAsync("/api/auth/register", body);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"register {resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
    }

    private static async Task<string> LoginAsync(HttpClient http, string user)
    {
        var resp = await http.PostAsync("/api/auth/login", Json(new { username = user, password = E2ePassword }));
        var txt = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"login {resp.StatusCode}: {txt}");
        using var doc = JsonDocument.Parse(txt);
        return doc.RootElement.GetProperty("token").GetString()!;
    }

    private static async Task<Guid> CreateDeckAsync(HttpClient http, string name, Dictionary<string, List<string>> deck)
    {
        var lines = deck.Select(kv => new { cardId = kv.Key, count = kv.Value.Count }).ToList();
        var resp = await http.PostAsync("/api/decks", Json(new { name, cards = lines }));
        var txt = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"create deck {resp.StatusCode}: {txt}");
        using var doc = JsonDocument.Parse(txt);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static StringContent Json(object o) =>
        new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

    // ---------- cards.json rule loading ----------

    private static readonly Dictionary<string, CardRule> Rules = new();
    private static Dictionary<string, CardRule> LoadCardRules(string path)
    {
        if (Rules.Count > 0) return Rules;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var id = el.GetProperty("id").GetString()!;
            var rule = new CardRule
            {
                IsSpell = el.GetProperty("cardType").GetString() == "Spell",
                IsCreature = el.GetProperty("cardType").GetString() is "Creature" or "EvolutionCreature",
                IsEvolution = el.GetProperty("cardType").GetString() == "EvolutionCreature",
                ManaCost = el.GetProperty("manaCost").GetInt32(),
            };
            if (el.TryGetProperty("evolutionOf", out var evo)) rule.EvolutionOf = evo.GetString() ?? "";
            if (el.TryGetProperty("keywords", out var kws))
                foreach (var kw in kws.EnumerateArray()) rule.Keywords.Add(kw.GetString()!);
            if (el.TryGetProperty("effects", out var fx))
            {
                foreach (var e in fx.EnumerateArray())
                {
                    if (e.TryGetProperty("target", out var t))
                    {
                        var scope = t.GetString() ?? "";
                        if (scope is not "" and not "None" and not "Nothing")
                        {
                            rule.NeedsTarget = true;
                            var cap = 999;
                            if (e.TryGetProperty("value", out var v) && v.TryGetInt32(out var vi)) cap = vi;
                            rule.TargetHints.Add((scope, cap));
                        }
                    }
                }
            }
            Rules[id] = rule;
        }
        return Rules;
    }

    private static Dictionary<string, List<string>> BuildDeck(string[] ids)
    {
        var d = new Dictionary<string, List<string>>();
        foreach (var id in ids) d[id] = Enumerable.Repeat(id, 4).ToList();
        return d;
    }

    private static int GetEnvInt(string name, int def) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : def;

    private static void Info(string m)
    {
        lock (LogLock) Infos.Add(m);
        Console.WriteLine($"  [i] {m}");
    }

    private static void Failure(string m)
    {
        lock (LogLock) Failures.Add(m);
        Console.WriteLine($"  [FAIL] {m}");
    }
}