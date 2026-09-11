// DuelE2E - headless end-to-end harness for the online duel backend.
//
// Drives real SignalR clients through the full online flow against a
// running backend + PostgreSQL: registers a user, creates a saved deck via
// /api/decks, hosts/joins a match with saved decks, then plays a scripted
// game while asserting hand redaction, saved-deck usage, two-phase blocking,
// general turn progression, and the two decision tap abilities (Adomis
// shield-look and Garatyano deck scry/reorder) end-to-end. A second scenario
// repeats the host side against the SERVER-SIDE MatchBot (vs-AI host): the
// backend seats its own AiController pilot, driven through the same match
// gates (block windows, shield triggers, whole turns) as the interactive
// clients, and the harness verifies the bot actually develops and takes turns.
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
    public string OpponentName = "";
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
    public bool HasDecisionTargets;
    public bool PeekDone;
    public bool ScryStarted;
    public bool ScryDone;
    public bool ScryWindowOwnerSeen;
    public bool ScryWindowOpponentSeen;
    public bool ScryClosedOk;

    /// <summary>Crafted deck used to open a match; observed cards are checked against it.</summary>
    public Dictionary<string, List<string>>? CraftedDeck;
    public readonly HashSet<string> SeenIds = new();
    public bool SawForeignCard;
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

    private const string AdomisId = "dm_06_011";
    private const string GaratyanoId = "dm_07_021";

    private static readonly Dictionary<string, List<string>> HostDeck = BuildDeck(new[]
    {
        // Light + Water mix so the deck can actually summon both decision
        // creatures: Adomis (Light shield-look) and Garatyano (Water scry).
        "dm_01_009", "dm_01_003", "dm_01_008", "dm_01_002", "dm_01_006",
        "dm_01_016", "dm_01_023", "dm_01_025", AdomisId, GaratyanoId,
    });
    private static readonly Dictionary<string, List<string>> JoinerDeck = BuildDeck(new[]
    {
        "dm_01_007", "dm_01_016", "dm_01_012", "dm_01_001", "dm_01_004",
        "dm_01_010", "dm_01_015", "dm_01_002", "dm_01_009", "dm_02_006",
    });

    private static readonly List<string> Failures = new();
    private static readonly List<string> Infos = new();
    private static readonly object LogLock = new();
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

            // --- two-player decision scenario. Whether a single random opening
            // draws and summons both decision creatures (Adomis peek, Garatyano
            // scry) is luck, so replay the whole match on a fresh room until both
            // round-trips are exercised (bounded). Scenario failures that also
            // recur on the final attempt are kept and fail the run.
            var scenarioOk = false;
            for (var attempt = 1; attempt <= 3 && !scenarioOk; attempt++)
            {
                var attemptStart = Failures.Count;
                if (attempt > 1)
                    Info($"two-player scenario retry #{attempt}: fresh random match to exercise both decision abilities...");
                await RunTwoPlayerScenarioAsync(http, hostDeckId, joinerDeckId);
                if (Failures.Count == attemptStart)
                {
                    scenarioOk = true;
                }
                else if (attempt < 3)
                {
                    Failures.RemoveRange(attemptStart, Failures.Count - attemptStart);
                }
            }

            // --- vs-AI: one real client against the server-side MatchBot ---
            await RunVsAiScenarioAsync(http);

            // --- reconnect: a dropped seat reclaims its match via RejoinMatch ---
            await RunReconnectScenarioAsync(http);

            // --- rematch: a finished match restarts in place (vs-AI auto-accepts) ---
            await RunRematchScenarioAsync(http);
        }
        catch (Exception ex)
        {
            Failure($"harness exception: {ex}");
        }

        return Finish();
    }

    /// <summary>
    /// Drive a full two-player match between two scripted bots with saved decks,
    /// asserting redaction, deck fidelity, decided blocking, turn progression,
    /// and both decision tap abilities (Adomis shield-look, Garatyano scry).
    /// Any passed-flag, redaction, foreign-card, turn-progression, or decision
    /// round-trip defect is recorded via <see cref="Failure"/>.
    /// </summary>
    private static async Task RunTwoPlayerScenarioAsync(HttpClient http, Guid hostDeckId, Guid joinerDeckId)
    {
        // --- connections ---
        var host = new Bot { Name = "E2E_Host", Conn = NewConnection(), HasDecisionTargets = true, CraftedDeck = HostDeck };
        var joiner = new Bot { Name = "E2E_Joiner", Conn = NewConnection(), CraftedDeck = JoinerDeck };
        Wire(host);
        Wire(joiner);

        Info("connecting host...");
        await host.Conn.StartAsync();
        Info("connecting joiner...");
        await joiner.Conn.StartAsync();

        // --- host match with saved deck ---
        Info("hosting match with saved deck...");
        var hostInfo = await host.Conn.InvokeAsync<MatchInfo>(DuelContract.Hub.HostMatch, "E2E host", hostDeckId, false);
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
            return;
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

            // Scry window: the host opened it with Garatyano; resolve it before
            // any further actions (the engine gates everything while it is open).
            if (sA.ScryWindowActive && host.ScryStarted && !host.ScryDone)
            {
                if (await HandleScryAsync(host, joiner))
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

        if (host.SawForeignCard)
            Failure("host drew/used a card NOT in its crafted saved deck -> deck load may have fallen back to random");
        else
            Info($"host deck verified: only crafted-card ids observed across zones ({host.SeenIds.Count} used)");

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

        if (host.PeekDone)
            Info("shield-look (Adomis) exercised: peeked shield card returned face-up to the caller");
        else
            Failure("shield-look (Adomis) round-trip never completed");

        if (host.ScryDone)
            Info($"scry (Garatyano) exercised: owner saw {host.ScryWindowOwnerSeen}, opponent saw {joiner.ScryWindowOpponentSeen}, window closed {host.ScryClosedOk}");
        else
            Failure("scry (Garatyano) round-trip never completed");

        if (host.StateCount > 0 && joiner.StateCount > 0)
            Info($"states received: host={host.StateCount} joiner={joiner.StateCount}");
    }

    private static async Task RunVsAiScenarioAsync(HttpClient http)
    {
        var user = "e2e_ai_" + Guid.NewGuid().ToString("N")[..10];
        Info($"registering vs-AI user {user}");
        await RegisterAsync(http, user);
        var token = await LoginAsync(http, user);
        var auth = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(20) };
        auth.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var deckId = await CreateDeckAsync(auth, "E2E VS AI", JoinerDeck);
        Info($"vs-AI deck created: {deckId}");

        var host = new Bot { Name = "E2E_VsAi", Conn = NewConnection(), CraftedDeck = JoinerDeck };
        Wire(host);
        Info("connecting vs-AI host...");
        await host.Conn.StartAsync();

        var hostInfo = await host.Conn.InvokeAsync<MatchInfo>(DuelContract.Hub.HostMatch, "E2E vs AI host", deckId, true);
        host.Side = hostInfo.YourSide;
        Info($"vs-AI host assigned side {host.Side}, code {hostInfo.MatchCode}, opponent {hostInfo.OpponentName}");
        if (string.IsNullOrWhiteSpace(hostInfo.OpponentName))
            Failure("vs-AI match reported no opponent name (no bot seated)");
        else
            Info($"vs-AI opponent is {hostInfo.OpponentName}");

        if (!await WaitForStateAsync(host, 10))
        {
            Failure("vs-AI host never got an initial DuelGameState (bot game did not start)");
            return;
        }
        Info("vs-AI initial state received (server seated the bot and started the engine)");
        VerifyHandRedaction(host);

        var deadline = DateTime.UtcNow.AddSeconds(GetEnvInt("E2E_DURATION", 150));
        var idleStart = DateTime.UtcNow;
        var lastAnyEvent = DateTime.UtcNow;
        var gameOverReported = false;
        var aiDevelopment = 0;

        while (DateTime.UtcNow < deadline)
        {
            var s = host.Latest;
            if (s is null)
            {
                await Task.Delay(100);
                continue;
            }
            Track(host, s);

            if (host.LastEvent.ToBinary() > lastAnyEvent.ToBinary())
            {
                lastAnyEvent = DateTime.FromBinary(host.LastEvent.ToBinary());
                idleStart = DateTime.UtcNow;
            }
            if ((DateTime.UtcNow - idleStart).TotalSeconds > 45)
            {
                Failure($"vs-AI deadlock: no state change for ~45s (turn {s.TurnNumber}, phase {s.Phase}, gameOver={s.IsGameOver})");
                break;
            }

            // How far the server-side AI developed (mana + battle zone visible to the host).
            var aiPlayer = s.Players.FirstOrDefault(p => p.Side != host.Side);
            if (aiPlayer is not null)
            {
                var dev = aiPlayer.ManaZone.Count(c => !c.CountOnly) + aiPlayer.BattleZone.Count(c => !c.CountOnly);
                if (dev > aiDevelopment) aiDevelopment = dev;
            }

            if (s.IsGameOver)
            {
                gameOverReported = true;
                Info($"vs-AI game over reported (winnerId={s.WinnerId}) after turn {s.TurnNumber}");
                break;
            }

            // Shield Trigger window owned by the host (the AI resolves its own windows).
            if (s.ShieldTriggerOwnerSide == host.Side && await HandleOwnerOnly(host, s))
            {
                await BumpAsync(host, host);
                continue;
            }

            // Pending block where the host is the defender (the AI resolves its own).
            if (s.AttackPending && !s.YourTurn && await HandleBlockDecision(host, s))
            {
                await BumpAsync(host, host);
                continue;
            }

            if (!s.YourTurn)
            {
                await Task.Delay(80); // the server-side AI is playing; wait for broadcasts
                continue;
            }

            switch (s.Phase)
            {
                case "Untap": await TryInvoke(host, DuelContract.Hub.StartTurn); break;
                case "Draw": await TryInvoke(host, DuelContract.Hub.Draw); break;
                case "Main": if (!await DoMain(host, s)) await TryInvoke(host, DuelContract.Hub.EndMainPhase); break;
                case "End": await TryInvoke(host, DuelContract.Hub.EndTurn); break;
            }
            await BumpAsync(host, host);
        }

        Info($"vs-AI loop ended: gameOver={gameOverReported}, host turns={host.MaxTurnSeen}, ai development (mana+battle)={aiDevelopment}");
        if (!gameOverReported)
            Info("vs-AI match did not finish within the loop (acceptable if flow was verified)");

        if (host.Errors.Count > 0)
        {
            Info($"vs-AI errors sent to the host client: {host.Errors.Count}");
            foreach (var e in host.Errors.Take(5)) Info($"  vs-AI host err: {e}");
        }

        if (host.MaxTurnSeen >= 3)
            Info($"vs-AI turns progressed (host saw turn {host.MaxTurnSeen}); the AI took its turns");
        else
            Failure($"vs-AI never progressed past {host.MaxTurnSeen} turns - the server-side bot appears not to have driven its turns");

        if (aiDevelopment <= 0)
            Failure("vs-AI opponent never developed any mana or creatures -> the bot did not actually play");
        else
            Info($"vs-AI opponent developed {aiDevelopment} cards into its mana/battle zones");

        if (host.SawForeignCard)
            Failure("vs-AI host drew/used a card NOT in its crafted saved deck");
        else
            Info($"vs-AI host deck verified: only crafted-card ids observed ({host.SeenIds.Count} used)");

        if (host.StateCount > 0)
            Info($"vs-AI states received by the host client: {host.StateCount}");
    }

    /// <summary>
    /// Shared scripted driver used by the reconnect scenario: plays real turns on
    /// both seats (same policies as the main loop) until the joiner has reached the
    /// requested number of its own turns, the game ends, or the timeout expires.
    /// Returns false only when the loop expired without reaching the turn target.
    /// </summary>
    private static async Task<bool> DriveScriptedTurnsAsync(Bot host, Bot joiner, int targetJoinerTurns, int timeoutSec)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            var sA = host.Latest;
            var sB = joiner.Latest;
            if (sA is null || sB is null)
            {
                await Task.Delay(80);
                continue;
            }
            Track(host, sA);
            Track(joiner, sB);
            if (joiner.MaxTurnSeen >= targetJoinerTurns)
                return true;
            if (sA.IsGameOver || sB.IsGameOver)
            {
                Info($"scripted play hit game over before the turn target (turn {sA.TurnNumber})");
                return true;
            }

            if (sA.ShieldTriggerOwnerSide is { } ownerSide)
            {
                var owner = ownerSide == host.Side ? host : joiner;
                var stO = owner.Latest;
                if (stO is not null && await HandleOwnerOnly(owner, stO))
                {
                    await Task.Delay(200);
                    continue;
                }
            }
            if (sA.ScryWindowActive && host.ScryStarted && !host.ScryDone)
            {
                if (await HandleScryAsync(host, joiner))
                {
                    await Task.Delay(200);
                    continue;
                }
            }

            var defender = sA.ActiveSide == host.Side ? joiner : host;
            var stD = defender.Latest;
            if (stD is not null && await HandleBlockDecision(defender, stD))
            {
                await Task.Delay(200);
                continue;
            }

            var active = sA.ActiveSide == host.Side ? host : joiner;
            var st = active.Latest;
            if (st is null || !st.YourTurn)
            {
                await Task.Delay(80);
                continue;
            }

            switch (st.Phase)
            {
                case "Untap": await TryInvoke(active, DuelContract.Hub.StartTurn); break;
                case "Draw": await TryInvoke(active, DuelContract.Hub.Draw); break;
                case "Main": if (!await DoMain(active, st)) await TryInvoke(active, DuelContract.Hub.EndMainPhase); break;
                case "End": await TryInvoke(active, DuelContract.Hub.EndTurn); break;
            }
            await Task.Delay(200);
        }
        return false; // loop expired - treat as a stall
    }

    /// <summary>
    /// Mid-match transport recovery: the joiner's connection is stopped hard while
    /// the game is in progress (the server keeps the room), then a fresh connection
    /// reclaims the exact same seat via RejoinMatch and the match resumes play.
    /// </summary>
    private static async Task RunReconnectScenarioAsync(HttpClient http)
    {
        var user = "e2e_rec_" + Guid.NewGuid().ToString("N")[..10];
        Info($"registering reconnect user {user}");
        await RegisterAsync(http, user);
        var token = await LoginAsync(http, user);
        var auth = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(20) };
        auth.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var hostDeckId = await CreateDeckAsync(auth, "E2E Rec Host", HostDeck);
        var joinerDeckId = await CreateDeckAsync(auth, "E2E Rec Joiner", JoinerDeck);

        var host = new Bot { Name = "E2E_RecHost", Conn = NewConnection(), HasDecisionTargets = true, CraftedDeck = HostDeck };
        var joiner = new Bot { Name = "E2E_RecJoiner", Conn = NewConnection(), CraftedDeck = JoinerDeck };
        Wire(host);
        Wire(joiner);
        await host.Conn.StartAsync();
        await joiner.Conn.StartAsync();

        var hostInfo = await host.Conn.InvokeAsync<MatchInfo>(DuelContract.Hub.HostMatch, "E2E rec host", hostDeckId, false);
        host.Side = hostInfo.YourSide;
        host.MatchCode = hostInfo.MatchCode;
        var joinInfo = await joiner.Conn.InvokeAsync<MatchInfo>(DuelContract.Hub.JoinMatch, hostInfo.MatchCode, "E2E rec joiner", joinerDeckId);
        joiner.Side = joinInfo.YourSide;
        joiner.MatchCode = hostInfo.MatchCode;
        Info($"reconnect match started: host={host.Side} joiner={joiner.Side} code={hostInfo.MatchCode}");

        if (!await WaitForStateAsync(host, 10) || !await WaitForStateAsync(joiner, 10))
        {
            Failure("reconnect setup: both clients never got an initial DuelGameState");
            return;
        }

        // Play a couple of scripted turns first so there is real in-progress state.
        if (!await DriveScriptedTurnsAsync(host, joiner, targetJoinerTurns: 2, timeoutSec: 30))
        {
            Failure("reconnect setup: scripted play deadlocked before the drop");
            return;
        }

        var turnAtDrop = joiner.MaxTurnSeen;
        Info($"dropping the joiner mid-match (joiner saw turn {turnAtDrop})...");
        await joiner.Conn.StopAsync();
        await Task.Delay(1000);

        // A completely fresh connection (no automatic-reconnect resume) reclaims the seat.
        joiner.Conn = NewConnection();
        Wire(joiner);
        await joiner.Conn.StartAsync();
        Info("joiner reconnected on a fresh connection; calling RejoinMatch...");

        MatchInfo? rejoinInfo = null;
        var rejoinDeadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < rejoinDeadline)
        {
            try
            {
                rejoinInfo = await joiner.Conn.InvokeAsync<MatchInfo>(DuelContract.Hub.RejoinMatch, joiner.MatchCode, joiner.Side);
                if (rejoinInfo is not null)
                    break;
            }
            catch (Exception)
            {
                // Server may still be settling the disconnect; retry.
            }
            await Task.Delay(300);
        }

        if (rejoinInfo is null)
        {
            Failure("rejoin failed: RejoinMatch never reclaimed the seat (seat may appear still-active)");
            return;
        }
        if (!string.Equals(rejoinInfo.YourSide, joinInfo.YourSide, StringComparison.Ordinal))
            Failure($"rejoin changed the side: was {joinInfo.YourSide}, got {rejoinInfo.YourSide}");
        joiner.Side = rejoinInfo.YourSide;
        Info($"rejoined as {rejoinInfo.YourSide} (opponent {rejoinInfo.OpponentName})");

        if (!await WaitForStateAsync(joiner, 10))
        {
            Failure("rejoin: no state broadcast reached the rejoined client");
            return;
        }
        if (joiner.Latest is null || joiner.Latest.TurnNumber < turnAtDrop)
            Failure($"rejoin resumed behind the drop point (at turn {joiner.Latest?.TurnNumber}, dropped at {turnAtDrop}) - the match did not resume");
        else
            Info($"rejoin state resumed at turn {joiner.Latest.TurnNumber}");

        // Prove the reclaimed seat is live: drive at least one more turn past the drop.
        var targetAfter = turnAtDrop + 1;
        if (!await DriveScriptedTurnsAsync(host, joiner, targetJoinerTurns: targetAfter, timeoutSec: 30))
            Failure($"rejoin: scripted play deadlocked after the rejoin (stuck at turn {joiner.MaxTurnSeen})");
        else if (joiner.MaxTurnSeen >= targetAfter)
            Info($"rejoined joiner kept playing (reached turn {joiner.MaxTurnSeen})");
        else
            Failure($"rejoined joiner never advanced past turn {turnAtDrop} (at {joiner.MaxTurnSeen})");

        if (host.Errors.Count > 0 || joiner.Errors.Count > 0)
            Info($"reconnect scenario client errors: host={host.Errors.Count} joiner={joiner.Errors.Count}");
    }

    /// <summary>
    /// Rematch restart: a vs-AI match is played to a real game over with the human
    /// merely passing its turns to let the bot win quickly, then RequestRematch must
    /// restart in place immediately (the bot auto-accepts) and a second mid-game
    /// request must be refused.
    /// </summary>
    private static async Task RunRematchScenarioAsync(HttpClient http)
    {
        var user = "e2e_rem_" + Guid.NewGuid().ToString("N")[..10];
        Info($"registering rematch user {user}");
        await RegisterAsync(http, user);
        var token = await LoginAsync(http, user);
        var auth = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(20) };
        auth.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var deckId = await CreateDeckAsync(auth, "E2E Rematch", JoinerDeck);

        var host = new Bot { Name = "E2E_Rematch", Conn = NewConnection(), CraftedDeck = JoinerDeck };
        Wire(host);
        await host.Conn.StartAsync();

        var hostInfo = await host.Conn.InvokeAsync<MatchInfo>(DuelContract.Hub.HostMatch, "E2E rematch host", deckId, true);
        host.Side = hostInfo.YourSide;
        host.MatchCode = hostInfo.MatchCode;
        Info($"rematch match started: side {host.Side}, code {hostInfo.MatchCode}, opponent {hostInfo.OpponentName}");
        if (string.IsNullOrWhiteSpace(hostInfo.OpponentName))
            Failure("rematch match reported no opponent name (no bot seated)");

        if (!await WaitForStateAsync(host, 10))
        {
            Failure("rematch setup: host never got an initial DuelGameState");
            return;
        }

        var deadline = DateTime.UtcNow.AddSeconds(GetEnvInt("E2E_DURATION", 150));
        var idleStart = DateTime.UtcNow;
        var lastAnyEvent = DateTime.UtcNow;
        var gameOverReported = false;

        while (DateTime.UtcNow < deadline)
        {
            var s = host.Latest;
            if (s is null)
            {
                await Task.Delay(100);
                continue;
            }
            Track(host, s);
            if (host.LastEvent.ToBinary() > lastAnyEvent.ToBinary())
            {
                lastAnyEvent = DateTime.FromBinary(host.LastEvent.ToBinary());
                idleStart = DateTime.UtcNow;
            }
            if ((DateTime.UtcNow - idleStart).TotalSeconds > 45)
            {
                Failure($"rematch setup deadlocked (turn {s.TurnNumber}, phase {s.Phase}, gameOver={s.IsGameOver})");
                break;
            }
            if (s.IsGameOver)
            {
                gameOverReported = true;
                Info($"rematch game over reported (winnerId={s.WinnerId}) after turn {s.TurnNumber}");
                break;
            }
            if (s.ShieldTriggerOwnerSide == host.Side && await HandleOwnerOnly(host, s))
            {
                await Task.Delay(200);
                continue;
            }
            if (s.AttackPending && !s.YourTurn && await HandleBlockDecision(host, s))
            {
                await Task.Delay(200);
                continue;
            }
            if (!s.YourTurn)
            {
                await Task.Delay(100);
                continue;
            }
            // The human passes the whole turn so the bot wins quickly.
            switch (s.Phase)
            {
                case "Untap": await TryInvoke(host, DuelContract.Hub.StartTurn); break;
                case "Draw": await TryInvoke(host, DuelContract.Hub.Draw); break;
                case "Main": await TryInvoke(host, DuelContract.Hub.EndMainPhase); break;
                case "End": await TryInvoke(host, DuelContract.Hub.EndTurn); break;
            }
            await Task.Delay(200);
        }

        if (!gameOverReported)
        {
            Failure("rematch test: the vs-AI game never ended within the window - cannot exercise a restart");
            return;
        }

        var genBeforeRematch = host.LastStateGen;
        var restarted = await host.Conn.InvokeAsync<bool>(DuelContract.Hub.RequestRematch);
        if (!restarted)
        {
            Failure($"rematch request did not restart: returned false (errors: {string.Join("; ", host.Errors)})");
            return;
        }
        Info("rematch request returned true (the bot auto-accepted)");

        var freshOk = false;
        var freshDeadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < freshDeadline)
        {
            var s = host.Latest;
            if (s is { IsGameOver: false, TurnNumber: 1 } && host.LastStateGen > genBeforeRematch)
            {
                freshOk = true;
                break;
            }
            await Task.Delay(80);
        }
        if (!freshOk)
            Failure("rematch: no fresh turn-1 state arrived after the restart");
        else
            Info($"rematch restart observed: fresh turn-1 game, side {host.Latest!.YourSide}");

        // A second request mid-game must be refused (the match has not ended again).
        var refused = await host.Conn.InvokeAsync<bool>(DuelContract.Hub.RequestRematch);
        if (refused)
            Failure("rematch: a mid-game second request unexpectedly restarted the match");
        else
            Info("rematch mid-game refusal verified (returned false)");
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
        if (b.CraftedDeck is null) return;
        foreach (var p in s.Players)
        {
            if (p.Side != b.Side) continue;
            foreach (var c in p.Hand.Concat(p.ManaZone).Concat(p.BattleZone).Concat(p.Graveyard))
            {
                if (c.CountOnly || string.IsNullOrEmpty(c.CardId)) continue;
                lock (LogLock)
                {
                    b.SeenIds.Add(c.CardId);
                    if (!b.CraftedDeck.ContainsKey(c.CardId)) b.SawForeignCard = true;
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

    private static async Task<bool> HandleScryAsync(Bot owner, Bot foe)
    {
        var stO = owner.Latest;
        if (stO is null || !stO.ScryWindowActive || stO.ScryOwnerSide != owner.Side)
            return false;
        var cards = stO.ScryCards;
        if (cards.Count != 3 || cards.Any(c => c.CountOnly || string.IsNullOrEmpty(c.CardId) || string.IsNullOrEmpty(c.InstanceId)))
        {
            Failure($"scry window exposes bad cards to owner (count={cards.Count})");
            return true;
        }
        owner.ScryWindowOwnerSeen = true;
        Info($"scry (Garatyano) owner sees: {string.Join(", ", cards.Select(c => c.Name))}");

        // The opponent must see the window active but never a single card.
        var foeObserved = await WaitForScryWindowAsync(foe, 12);
        var stF = foe.Latest;
        if (!foeObserved || stF is null || stF.ScryOwnerSide != owner.Side || stF.ScryCards.Count != 0)
            Failure("scry redaction broken: opponent never saw the active-but-empty window");
        else
        {
            foe.ScryWindowOpponentSeen = true;
            Info("scry (Garatyano) opponent sees only the active window (zero cards)");
        }

        // Reorder the deck and close the window using the "Scry:{i}" instance tokens.
        var tokens = cards.Select(c => c.InstanceId).ToList();
        tokens.Reverse();
        try
        {
            await owner.Conn.InvokeCoreAsync(DuelContract.Hub.SubmitScryOrder, new object[] { tokens });
            owner.LastEvent = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Failure($"scry SubmitScryOrder invoke failed: {ex}");
            return true;
        }
        if (await WaitForScryClosedAsync(owner, 12))
        {
            owner.ScryClosedOk = true;
            owner.ScryDone = true;
            Info("scry (Garatyano) reordered deck and the window closed");
        }
        else
        {
            Failure("scry window did not close after SubmitScryOrder");
        }
        return true;
    }

    private static async Task<bool> WaitForScryWindowAsync(Bot b, int timeoutSec)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            var s = b.Latest;
            if (s is { ScryWindowActive: true } && !string.IsNullOrEmpty(s.ScryOwnerSide)) return true;
            await Task.Delay(50);
        }
        return b.Latest is { ScryWindowActive: true };
    }

    private static async Task<bool> WaitForScryClosedAsync(Bot b, int timeoutSec)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            if (b.Latest is { ScryWindowActive: false }) return true;
            await Task.Delay(50);
        }
        return false;
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

        // Decision tap abilities (host deck only): activate the shield-look and
        // scry creatures as soon as they are ready, then keep playing normally.
        bool IsTapTarget(CardState c) =>
            b.HasDecisionTargets && (c.CardId == AdomisId || c.CardId == GaratyanoId);
        if (b.HasDecisionTargets)
        {
            if (!b.PeekDone)
            {
                var aIdx = me.BattleZone.FindIndex(c =>
                    c.CardId == AdomisId && c.HasTapAbility && c.CanUseTapAbility && c.TapDecisionKind == "shield");
                if (aIdx >= 0 && me.ShieldCount > 0)
                {
                    CardState? peeked;
                    try
                    {
                        peeked = await b.Conn.InvokeAsync<CardState?>(DuelContract.Hub.ActivateTapAbilityShield, aIdx, 0);
                        b.LastEvent = DateTime.UtcNow;
                    }
                    catch (Exception ex)
                    {
                        Failure($"shield-look invoke failed: {ex}");
                        peeked = null;
                    }
                    if (peeked is null || peeked.CountOnly || string.IsNullOrEmpty(peeked.Name))
                        Failure($"shield-look returned nothing usable: null={peeked is null}");
                    else
                    {
                        Info($"shield-look (Adomis) peeked shield[0] = {peeked.Name} ({peeked.CardId})");
                        b.PeekDone = true;
                    }
                    return true;
                }
            }
            if (!b.ScryDone && !b.ScryStarted)
            {
                var gIdx = me.BattleZone.FindIndex(c =>
                    c.CardId == GaratyanoId && c.HasTapAbility && c.CanUseTapAbility && c.TapDecisionKind == "scry");
                if (gIdx >= 0)
                {
                    await TryInvoke(b, DuelContract.Hub.ActivateTapAbilityScry, gIdx);
                    b.ScryStarted = true;
                    return true;
                }
            }
        }

        var untappedByCiv = me.ManaZone
            .Where(c => !c.CountOnly && !c.IsTapped)
            .GroupBy(c => c.Civilization)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        bool CivOk(CardState c) =>
            !string.IsNullOrEmpty(c.Civilization) &&
            untappedByCiv.TryGetValue(c.Civilization, out var n) && n >= 1;

        if (st.CanPlayMana && me.UntappedMana < 12)
        {
            // The host never burns its decision creatures as mana while other
            // cards suffice, and it keeps both the Water (Garatyano) and the
            // Light (Adomis) pools able to pay their costs once they are drawn.
            var wantCiv = "Light";
            if (b.HasDecisionTargets)
            {
                // Water up to 3 for Garatyano, then Light up to 3 for Adomis,
                // then keep the two sides balanced so either can be summoned.
                var water = untappedByCiv.TryGetValue("Water", out var w) ? w : 0;
                var light = untappedByCiv.TryGetValue("Light", out var l) ? l : 0;
                wantCiv = water < 3 ? "Water" : light < 3 ? "Light" : (water < light ? "Water" : "Light");
            }
            else
            {
                var cheapestCiv = me.Hand.Count == 0
                    ? null
                    : me.Hand.Where(c => !c.CountOnly).GroupBy(c => c.Civilization)
                        .OrderBy(g => untappedByCiv.TryGetValue(g.Key, out var n) ? n : 0)
                        .ThenByDescending(g => g.Count())
                        .Select(g => g.Key).FirstOrDefault();
                wantCiv = cheapestCiv ?? "Light";
            }
            var i = me.Hand.FindIndex(c => !c.CountOnly && !IsTapTarget(c) &&
                string.Equals(c.Civilization, wantCiv, StringComparison.OrdinalIgnoreCase));
            if (i < 0) i = me.Hand.FindIndex(c => !c.CountOnly && !IsTapTarget(c));
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
        // Give the freshly invoked action time to arrive as a new broadcast; the
        // loop re-reads each client's latest snapshot next iteration, so a brief
        // fixed delay is all that is needed (the probe lived on the same pattern).
        await Task.Delay(200);
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
            b.OpponentName = mi.OpponentName ?? "";
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