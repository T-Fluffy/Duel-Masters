using System;
using System.Collections.Generic;
using System.Threading;
using DuelMasters.Domain;
using DuelMasters.Domain.Ai;
using DuelMasters.Domain.Networking;
using DuelMasters.Server.Services;

namespace DuelMasters.Server.Hubs;

/// <summary>
/// A live networked match. Holds the authoritative <see cref="DuelGame"/> (the rules
/// run only here) plus the routing of each DuelSide to a SignalR connection id and
/// display name. A single match gate lock serializes game mutations so concurrent
/// hub calls from either side cannot interleave.
/// </summary>
public sealed class MatchRoom
{
    public MatchRoom(string code, string hostConnectionId, string hostName, Guid? hostDeckId, Func<Guid, List<Card>?>? deckLoader)
    {
        Code = code;
        SideConnections[DuelSide.Player1] = hostConnectionId;
        SideNames[DuelSide.Player1] = hostName;
        _hostDeckId = hostDeckId;
        _deckLoader = deckLoader;
    }

    public string Code { get; }

    /// <summary>DuelSide ("Player1"/"Player2") -> SignalR connection id.</summary>
    public readonly Dictionary<string, string> SideConnections = new();

    public readonly Dictionary<string, string> SideNames = new();

    private readonly object _gate = new();
    private DuelGame? _game;
    private int? _pendingAttackerIndex;
    private int _pendingBlocksAvailable;
    private readonly Guid? _hostDeckId;
    private Guid? _joinerDeckId;
    private readonly Func<Guid, List<Card>?>? _deckLoader;

    public bool Started => _game is not null;
    public bool HasSecondPlayer => SideConnections.ContainsKey(DuelSide.Player2);
    public bool IsGameOver => _game is not null && _game.IsGameOver;

    /// <summary>The server-side AI pilot for a vs-AI match, if any.</summary>
    public MatchBot? Bot { get; private set; }

    /// <summary>True when the given side is seated by a server-side bot, not a real connection.</summary>
    public bool IsBotSide(string side) =>
        SideConnections.TryGetValue(side, out var connection) &&
        connection.StartsWith(MatchBot.ConnectionPrefix, StringComparison.Ordinal);

    public string? WinnerSide => _game is null || _game.Winner is null
        ? null
        : DuelSide.FromIndex(_game.Winner == _game.Player1 ? 0 : 1);

    public string? ConnectionSide(string connectionId)
    {
        foreach (var kv in SideConnections)
        {
            if (string.Equals(kv.Value, connectionId, StringComparison.Ordinal))
                return kv.Key;
        }
        return null;
    }

    /// <summary>The side whose turn it currently is, or null after the match ends.</summary>
    public string? ActiveSide()
    {
        DuelGame? game;
        lock (_gate)
        {
            game = _game;
        }
        if (game is null || game.IsGameOver)
            return null;
        return DuelSide.FromIndex(game.ActivePlayer == game.Player1 ? 0 : 1);
    }

    /// <summary>The side that must answer a pending blocking decision, or null.</summary>
    public string? BlockDecisionSide()
    {
        DuelGame? game;
        lock (_gate)
        {
            game = _game;
        }
        if (game is null || game.IsGameOver)
            return null;
        return DuelSide.FromIndex(game.Opponent == game.Player1 ? 0 : 1);
    }

    /// <summary>Register the second participant; returns false if already full.</summary>
    public bool TryAddSecond(string connectionId, string name, Guid? joinerDeckId = null)
    {
        lock (_gate)
        {
            if (HasSecondPlayer)
                return false;
            SideConnections[DuelSide.Player2] = connectionId;
            SideNames[DuelSide.Player2] = name;
            _joinerDeckId = joinerDeckId;
            return true;
        }
    }

    /// <summary>
    /// Seat a server-side AI bot as the second player (vs-AI match). The bot gets a
    /// random deck (its joiner deck is never supplied) and is driven by
    /// <see cref="MatchBot"/> once the game starts. Returns false if already full.
    /// </summary>
    public bool TryAddBot(string name)
    {
        lock (_gate)
        {
            if (HasSecondPlayer)
                return false;
            SideConnections[DuelSide.Player2] = MatchBot.ConnectionPrefix + Code;
            SideNames[DuelSide.Player2] = string.IsNullOrWhiteSpace(name) ? "AI (Standard)" : name;
            _joinerDeckId = null;
            return true;
        }
    }

    /// <summary>
    /// Start the authoritative engine. Each side uses its selected saved deck when
    /// one was passed (and still loads/validates), otherwise a random deck is built.
    /// </summary>
    public void StartGame()
    {
        lock (_gate)
        {
            var rng = new Random();
            var p1 = new Player(SideNames[DuelSide.Player1], BuildDeck(_hostDeckId, rng));
            var p2 = new Player(SideNames[DuelSide.Player2], BuildDeck(_joinerDeckId, rng));
            var game = new DuelGame(p1, p2, rng);
            game.StartGame(shuffle: true);
            _game = game;
            Bot = IsBotSide(DuelSide.Player2) ? new MatchBot(this, p2, DuelSide.Player2) : null;
        }
    }

    private List<Card> BuildDeck(Guid? deckId, Random rng)
    {
        if (deckId is { } id && _deckLoader is not null && _deckLoader(id) is { Count: 40 } saved)
            return saved;
        return MatchCardCatalog.BuildRandomDeck(rng);
    }

    /// <summary>
    /// Run a caller-supplied game mutation under the match gate. Returns true on
    /// success, or false (with the message) when the action was illegal.
    /// </summary>
    public bool Execute(Action<DuelGame> action, out string? error)
    {
        lock (_gate)
        {
            if (_game is null)
            {
                error = "The match has not started yet.";
                return false;
            }
            try
            {
                action(_game);
                error = null;
                return true;
            }
            catch (RuleViolationException ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// True while an attack is declared on a player and the defender may choose a
    /// Blocker creature or pass. The attack itself is not resolved until then.
    /// </summary>
    public bool HasPendingBlock
    {
        get
        {
            lock (_gate)
            {
                return _pendingAttackerIndex is not null;
            }
        }
    }

    /// <summary>Index of the attacker awaiting a blocking decision, or null when none.</summary>
    public int? PendingAttackerIndex
    {
        get
        {
            lock (_gate)
            {
                return _pendingAttackerIndex;
            }
        }
    }

    /// <summary>
    /// Declare a direct player attack. When the defender has at least one legal
    /// Blocker, the attack pauses for <see cref="BlockAttack"/>/<see cref="PassBlock"/>
    /// instead of resolving immediately. Returns true when the action was accepted.
    /// </summary>
    public (bool Ok, int BlocksAvailable, string? Error) DeclarePlayerAttack(int attackerIndex)
    {
        lock (_gate)
        {
            if (_game is null)
                return (false, 0, "The match has not started yet.");
            if (_game.IsGameOver)
                return (false, 0, "The match has already ended.");
            if (_pendingAttackerIndex is not null)
                return (false, 0, "An attack is already awaiting a blocking decision.");
            if (HasPendingShieldTriggers(_game))
                return (false, 0, "Resolve the pending Shield Trigger cards first.");

            try
            {
                _game.ValidatePlayerAttack(attackerIndex);
            }
            catch (RuleViolationException ex)
            {
                return (false, 0, ex.Message);
            }

            var blocks = _game.ReadyBlockerChoices(attackerIndex);
            if (blocks > 0)
            {
                _pendingAttackerIndex = attackerIndex;
                _pendingBlocksAvailable = blocks;
                return (true, blocks, null);
            }

            _game.AttackPlayer(attackerIndex);
            return (true, 0, null);
        }
    }

    /// <summary>Resolve a pending player attack by blocking it with the defender's creature.</summary>
    public (bool Ok, string? Error) BlockPendingAttack(int blockerIndex)
    {
        lock (_gate)
        {
            if (_game is null)
                return (false, "The match has not started yet.");
            if (_game.IsGameOver)
                return (false, "The match has already ended.");
            if (_pendingAttackerIndex is not int attackerIndex)
                return (false, "There is no attack awaiting a blocker.");
            if (HasPendingShieldTriggers(_game))
                return (false, "Resolve the pending Shield Trigger cards first.");

            try
            {
                _game.AttackPlayer(attackerIndex, _game.Opponent, blockerIndex);
            }
            catch (RuleViolationException ex)
            {
                return (false, ex.Message);
            }

            _pendingAttackerIndex = null;
            return (true, null);
        }
    }

    /// <summary>Pass on blocking a pending player attack, resolving it unblocked.</summary>
    public (bool Ok, string? Error) PassPendingAttack()
    {
        lock (_gate)
        {
            if (_game is null)
                return (false, "The match has not started yet.");
            if (_game.IsGameOver)
                return (false, "The match has already ended.");
            if (_pendingAttackerIndex is not int attackerIndex)
                return (false, "There is no attack awaiting a blocker.");
            if (HasPendingShieldTriggers(_game))
                return (false, "Resolve the pending Shield Trigger cards first.");

            try
            {
                _game.AttackPlayer(attackerIndex);
            }
            catch (RuleViolationException ex)
            {
                return (false, ex.Message);
            }

            _pendingAttackerIndex = null;
            return (true, null);
        }
    }

    private static bool HasPendingShieldTriggers(DuelGame game) => game.ShieldTriggerWindowActive;

    // ------------------------------------------------------------ targeting helpers

    /// <summary>Maps a DuelSide string to the authoritative player in this match.</summary>
    public Player? PlayerForSide(string? side)
    {
        DuelGame? game;
        lock (_gate)
        {
            game = _game;
        }
        if (game is null)
            return null;
        return side == DuelSide.Player1 ? game.Player1 : game.Player2;
    }

    /// <summary>The player whose pending Shield Trigger cards may be played right now.</summary>
    public Player? ShieldTriggerOwnerOf(Player? simpleGuardUnused = null)
    {
        DuelGame? game;
        lock (_gate)
        {
            game = _game;
        }
        return game?.ShieldTriggerOwner;
    }

    /// <summary>True when the given side's player may currently play their Shield Trigger cards.</summary>
    public bool IsShieldTriggerOwnerSide(string side)
    {
        var owner = ShieldTriggerOwnerOf();
        if (owner is null)
            return false;
        DuelGame? game;
        lock (_gate)
        {
            game = _game;
        }
        if (game is null)
            return false;
        return side == DuelSide.Player1 ? ReferenceEquals(owner, game.Player1) : ReferenceEquals(owner, game.Player2);
    }

    /// <summary>Build a viewer-relative state snapshot for the given side.</summary>
    public DuelGameState StateFor(string side)
    {
        DuelGame? game;
        int? pendingAttacker;
        int blocksAvailable;
        lock (_gate)
        {
            game = _game;
            pendingAttacker = _pendingAttackerIndex;
            blocksAvailable = _pendingBlocksAvailable;
        }
        DuelGameState state;
        if (game is null)
        {
            state = new DuelGameState { MatchCode = Code, YourSide = side };
        }
        else
        {
            state = DuelGameState.From(game, Code, side);
            if (pendingAttacker is int attackerIndex)
            {
                // The active player's attacking creature is awaiting a block decision.
                state.AttackPending = true;
                state.AttackPendingAttackerIndex = attackerIndex;
                state.BlocksAvailable = blocksAvailable;
            }
        }
        return state;
    }
}
