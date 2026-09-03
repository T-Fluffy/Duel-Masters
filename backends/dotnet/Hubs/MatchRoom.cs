using System;
using System.Collections.Generic;
using System.Threading;
using DuelMasters.Domain;
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
    public MatchRoom(string code, string hostConnectionId, string hostName)
    {
        Code = code;
        SideConnections[DuelSide.Player1] = hostConnectionId;
        SideNames[DuelSide.Player1] = hostName;
    }

    public string Code { get; }

    /// <summary>DuelSide ("Player1"/"Player2") -> SignalR connection id.</summary>
    public readonly Dictionary<string, string> SideConnections = new();

    public readonly Dictionary<string, string> SideNames = new();

    private readonly object _gate = new();
    private DuelGame? _game;

    public bool Started => _game is not null;
    public bool HasSecondPlayer => SideConnections.ContainsKey(DuelSide.Player2);
    public bool IsGameOver => _game is not null && _game.IsGameOver;

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

    /// <summary>Register the second participant; returns false if already full.</summary>
    public bool TryAddSecond(string connectionId, string name)
    {
        lock (_gate)
        {
            if (HasSecondPlayer)
                return false;
            SideConnections[DuelSide.Player2] = connectionId;
            SideNames[DuelSide.Player2] = name;
            return true;
        }
    }

    /// <summary>Build random starter decks and start the authoritative engine.</summary>
    public void StartGame()
    {
        lock (_gate)
        {
            var rng = new Random();
            var p1 = new Player(SideNames[DuelSide.Player1], MatchCardCatalog.BuildRandomDeck(rng));
            var p2 = new Player(SideNames[DuelSide.Player2], MatchCardCatalog.BuildRandomDeck(rng));
            var game = new DuelGame(p1, p2, rng);
            game.StartGame(shuffle: true);
            _game = game;
        }
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

    /// <summary>Build a viewer-relative state snapshot for the given side.</summary>
    public DuelGameState StateFor(string side)
    {
        DuelGame? game;
        lock (_gate)
        {
            game = _game;
        }
        return game is null
            ? new DuelGameState { MatchCode = Code, YourSide = side }
            : DuelGameState.From(game, Code, side);
    }
}
