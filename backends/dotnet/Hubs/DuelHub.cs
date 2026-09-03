using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuelMasters.Domain;
using DuelMasters.Domain.Networking;
using Microsoft.AspNetCore.SignalR;

namespace DuelMasters.Server.Hubs;

/// <summary>Server → client push contract for a live duel.</summary>
public interface IDuelClientContract
{
    Task ReceiveGameState(DuelGameState state);
    Task ReceiveActionError(string errorMessage);
    Task AnnounceWinner(string winnerSide);
    Task MatchJoined(MatchInfo info);
}

/// <summary>
/// Authoritative match hub. The <see cref="DuelGame"/> runs only here; clients send
/// high-level actions and receive viewer-relative <see cref="DuelGameState"/> snapshots.
/// </summary>
public sealed class DuelHub : Hub<IDuelClientContract>
{
    private const string GroupPrefix = "duel:";
    private static readonly ConcurrentDictionary<string, MatchRoom> ActiveMatches = new();

    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 6;

    // ------------------------------------------------------------ host / join

    public async Task<MatchInfo> HostMatch(string yourName)
    {
        var code = GenerateUniqueCode();
        var room = new MatchRoom(code, Context.ConnectionId, string.IsNullOrWhiteSpace(yourName) ? "Player 1" : yourName);
        ActiveMatches[code] = room;
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(code));

        return new MatchInfo
        {
            MatchCode = code,
            YourSide = DuelSide.Player1,
            YourName = room.SideNames[DuelSide.Player1],
            OpponentName = "",
        };
    }

    public async Task<MatchInfo?> JoinMatch(string matchCode, string yourName)
    {
        var code = matchCode?.Trim().ToUpperInvariant() ?? "";
        if (!ActiveMatches.TryGetValue(code, out var room))
        {
            await Clients.Caller.ReceiveActionError("No match found with that code.");
            return null;
        }

        if (!room.TryAddSecond(Context.ConnectionId, string.IsNullOrWhiteSpace(yourName) ? "Player 2" : yourName))
        {
            await Clients.Caller.ReceiveActionError("That match is already full.");
            return null;
        }

        room.StartGame();

        await Groups.AddToGroupAsync(Context.ConnectionId, Group(code));

        await BroadcastMatchJoined(room);
        await BroadcastState(room);
        return new MatchInfo
        {
            MatchCode = code,
            YourSide = DuelSide.Player2,
            YourName = room.SideNames[DuelSide.Player2],
            OpponentName = room.SideNames[DuelSide.Player1],
        };
    }

    // -------------------------------------------------------------- actions

    public async Task StartTurn() => await RunGameAction(room => room.StartTurn());

    public async Task Draw() => await RunGameAction(room => room.Draw());

    public async Task PlayMana(int handIndex) =>
        await RunGameAction(room => room.PlayManaToManaZone(handIndex));

    public async Task SummonCreature(int handIndex) =>
        await RunGameAction(room => room.SummonCreature(handIndex));

    public async Task CastSpell(int handIndex) =>
        await RunGameAction(room => room.CastSpell(handIndex));

    public async Task AttackPlayer(int attackerIndex)
    {
        await RunGameAction(room => room.AttackPlayer(attackerIndex));
    }

    public async Task AttackCreature(int attackerIndex, int targetIndex) =>
        await RunGameAction(room => room.AttackCreature(attackerIndex, targetIndex));

    public async Task EndMainPhase() => await RunGameAction(room => room.EndMainPhase());

    public async Task EndTurn() => await RunGameAction(room => room.EndTurn());

    // -------------------------------------------------------------- helpers

    private async Task RunGameAction(Action<DuelGame> action)
    {
        var mySide = ResolveSide(out var room);
        if (room is null)
        {
            await Clients.Caller.ReceiveActionError("You are not in an active match.");
            return;
        }
        if (mySide is null)
        {
            await Clients.Caller.ReceiveActionError("Your connection could not be matched to a player.");
            return;
        }

        var activeSide = room.ActiveSide();
        if (activeSide is null || !string.Equals(mySide, activeSide, StringComparison.Ordinal))
        {
            await Clients.Caller.ReceiveActionError("It is not your turn.");
            return;
        }

        if (!room.Execute(action, out var error))
        {
            await Clients.Caller.ReceiveActionError(error ?? "That action is not allowed right now.");
            return;
        }

        await BroadcastState(room);
        await MaybeAnnounceWinner(room);
    }

    private MatchRoom? ResolveRoom() =>
        ActiveMatches.Values.FirstOrDefault(r => r.ConnectionSide(Context.ConnectionId) is not null);

    private string? ResolveSide(out MatchRoom? room)
    {
        room = ResolveRoom();
        return room?.ConnectionSide(Context.ConnectionId);
    }

    private async Task BroadcastState(MatchRoom room)
    {
        foreach (var side in room.SideConnections.Keys)
        {
            var connectionId = room.SideConnections[side];
            await Clients.Client(connectionId).ReceiveGameState(room.StateFor(side));
        }
    }

    private async Task MaybeAnnounceWinner(MatchRoom room)
    {
        var winner = room.WinnerSide;
        if (winner is null)
            return;
        foreach (var connectionId in room.SideConnections.Values)
            await Clients.Client(connectionId).AnnounceWinner(winner);
        ActiveMatches.TryRemove(room.Code, out _);
    }

    private async Task BroadcastMatchJoined(MatchRoom room)
    {
        var hostId = room.SideConnections[DuelSide.Player1];
        await Clients.Client(hostId).MatchJoined(new MatchInfo
        {
            MatchCode = room.Code,
            YourSide = DuelSide.Player1,
            YourName = room.SideNames[DuelSide.Player1],
            OpponentName = room.SideNames[DuelSide.Player2],
        });
    }

    private static string Group(string code) => GroupPrefix + code;

    private static string GenerateUniqueCode()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var code = new string(
                System.Linq.Enumerable.Range(0, CodeLength)
                    .Select(_ => CodeAlphabet[Random.Shared.Next(CodeAlphabet.Length)])
                    .ToArray());
            if (ActiveMatches.ContainsKey(code))
                continue;
            return code;
        }
        throw new InvalidOperationException("Could not allocate a unique match code.");
    }
}
