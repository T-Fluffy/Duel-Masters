using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using DuelMasters.Domain;
using DuelMasters.Domain.Networking;
using DuelMasters.Server.Data;
using DuelMasters.Server.Services;
using DuelResult = DuelMasters.Server.Models.DuelResult;
using MatchRecord = DuelMasters.Server.Models.MatchRecord;
using PlayerProfile = DuelMasters.Server.Models.PlayerProfile;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>SignalR connection ids currently connected, so a seat can tell a
    /// live opponent from a dropped one when deciding whether a rejoin is legal.</summary>
    private static readonly ConcurrentDictionary<string, byte> LiveConnections = new();

    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 6;

    private readonly IServiceScopeFactory _scopeFactory;

    public DuelHub(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public override async Task OnConnectedAsync()
    {
        LiveConnections[Context.ConnectionId] = 0;
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        LiveConnections.TryRemove(Context.ConnectionId, out _);
        // Finished matches never get a second player back once both seats are gone:
        // sweep them so code-joined lobbies do not pile up. In-progress matches are
        // kept - a dropped side may reconnect and reclaim its seat via RejoinMatch.
        foreach (var room in ActiveMatches.Values.ToList())
        {
            if (room.IsGameOver && !RoomHasLiveSeat(room))
                ActiveMatches.TryRemove(room.Code, out _);
        }
        await base.OnDisconnectedAsync(exception);
    }

    private static bool RoomHasLiveSeat(MatchRoom room) =>
        room.SideConnections.Keys.Any(side => !room.IsBotSide(side) && LiveConnections.ContainsKey(room.SideConnections[side]));

    // ------------------------------------------------------------ host / join

    public async Task<MatchInfo> HostMatch(string yourName, Guid? deckId = null, bool vsAi = false)
    {
        var code = GenerateUniqueCode();
        var room = new MatchRoom(code, Context.ConnectionId, string.IsNullOrWhiteSpace(yourName) ? "Player 1" : yourName, deckId, LoadDeckById);
        room.SetSideUser(DuelSide.Player1, CurrentUserIdOrNull());
        await OpenMatchRecordAsync(code, room.SideUser(DuelSide.Player1), room.SideNames[DuelSide.Player1], deckId);
        ActiveMatches[code] = room;
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(code));

        if (vsAi)
        {
            // Seat the server-side AI as the second player and start immediately;
            // the opponent's name (and its whole first turn) arrive through the
            // match-joined broadcast and the bot drive in BroadcastState.
            room.TryAddBot("AI (Standard)");
            room.SetSideUser(DuelSide.Player2, null);
            room.StartGame();
            await BroadcastMatchJoined(room);
            await BroadcastState(room);
        }

        return new MatchInfo
        {
            MatchCode = code,
            YourSide = DuelSide.Player1,
            YourName = room.SideNames[DuelSide.Player1],
            OpponentName = vsAi ? room.SideNames[DuelSide.Player2] : "",
        };
    }

    public async Task<MatchInfo?> JoinMatch(string matchCode, string yourName, Guid? deckId = null)
    {
        var code = matchCode?.Trim().ToUpperInvariant() ?? "";
        if (!ActiveMatches.TryGetValue(code, out var room))
        {
            await Clients.Caller.ReceiveActionError("No match found with that code.");
            return null;
        }

        if (!room.TryAddSecond(Context.ConnectionId, string.IsNullOrWhiteSpace(yourName) ? "Player 2" : yourName, deckId))
        {
            await Clients.Caller.ReceiveActionError("That match is already full.");
            return null;
        }
        room.SetSideUser(DuelSide.Player2, CurrentUserIdOrNull());
        await FillJoinerAsync(code, room.SideUser(DuelSide.Player2), room.SideNames[DuelSide.Player2], deckId);

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

    /// <summary>
    /// Return to a match after the transport dropped: a new connection reclaims its
    /// previous seat (validated against the still-live set) and is pushed a fresh
    /// state. The opponent's flow is untouched; an in-progress match pauses where the
    /// dropped side was expected until it rejoins.
    /// </summary>
    public async Task<MatchInfo?> RejoinMatch(string matchCode, string yourSide)
    {
        var code = matchCode?.Trim().ToUpperInvariant() ?? "";
        if (!ActiveMatches.TryGetValue(code, out var room))
        {
            await Clients.Caller.ReceiveActionError("No match found with that code. It may have ended or the server may have restarted.");
            return null;
        }

        var sideKey = yourSide is not null && string.Equals(yourSide, DuelSide.Player2, StringComparison.OrdinalIgnoreCase)
            ? DuelSide.Player2
            : yourSide is not null && string.Equals(yourSide, DuelSide.Player1, StringComparison.OrdinalIgnoreCase)
                ? DuelSide.Player1
                : null;
        if (sideKey is null)
        {
            await Clients.Caller.ReceiveActionError("That side is not a valid seat in this match.");
            return null;
        }

        if (!room.SideConnections.TryGetValue(sideKey, out var occupiedId) || room.IsBotSide(sideKey))
        {
            await Clients.Caller.ReceiveActionError("You have no seat in that match.");
            return null;
        }
        var seatUser = room.SideUser(sideKey);
        if (seatUser.HasValue && seatUser != CurrentUserIdOrNull())
        {
            await Clients.Caller.ReceiveActionError("That seat belongs to another player.");
            return null;
        }
        if (string.Equals(occupiedId, Context.ConnectionId, StringComparison.Ordinal))
        {
            // Idempotent: already seated on this connection - just re-push the state.
            await BroadcastState(room);
            return new MatchInfo
            {
                MatchCode = code,
                YourSide = sideKey,
                YourName = room.SideNames[sideKey],
                OpponentName = room.SideNames[sideKey == DuelSide.Player1 ? DuelSide.Player2 : DuelSide.Player1],
            };
        }
        if (LiveConnections.ContainsKey(occupiedId))
        {
            await Clients.Caller.ReceiveActionError("Your seat is still occupied by an active connection.");
            return null;
        }

        room.Reseat(sideKey, Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(code));
        await BroadcastState(room);
        return new MatchInfo
        {
            MatchCode = code,
            YourSide = sideKey,
            YourName = room.SideNames[sideKey],
            OpponentName = room.SideNames[sideKey == DuelSide.Player1 ? DuelSide.Player2 : DuelSide.Player1],
        };
    }

    /// <summary>
    /// Ask for a rematch of the finished match. Returns true when this request
    /// actually restarted the game (the second request of a human pair, or any
    /// request in a vs-AI match); returns false when still waiting on the opponent.
    /// </summary>
    public async Task<bool> RequestRematch()
    {
        var mySide = ResolveSide(out var room);
        if (room is null || mySide is null)
        {
            await Clients.Caller.ReceiveActionError("You are not in an active match.");
            return false;
        }
        var (restarted, error) = room.RequestRematch(mySide);
        if (error is not null)
        {
            await Clients.Caller.ReceiveActionError(error);
            return false;
        }
        if (restarted)
        {
            await BroadcastState(room);
            await MaybeAnnounceWinner(room);
        }
        return restarted;
    }

    // -------------------------------------------------------------- actions

    public async Task StartTurn() => await RunGameAction(room => room.StartTurn());

    public async Task Draw() => await RunGameAction(room => room.Draw());

    public async Task PlayMana(int handIndex) =>
        await RunGameAction(room => room.PlayManaToManaZone(handIndex));

    public async Task SummonCreature(int handIndex) =>
        await RunGameAction(room => room.SummonCreature(handIndex));

    public async Task SummonCreatureTargeted(int handIndex, string targetOwnerSide, int targetIndex)
    {
        var player = ResolveRoom()?.PlayerForSide(targetOwnerSide);
        if (player is null)
        {
            await Clients.Caller.ReceiveActionError("That target is not available.");
            return;
        }
        await RunGameAction(game => game.SummonCreature(handIndex, player, targetIndex));
    }

    public async Task CastSpell(int handIndex) =>
        await RunGameAction(room => room.CastSpell(handIndex));

    public async Task CastSpellTargeted(int handIndex, string targetOwnerSide, int targetIndex)
    {
        var player = ResolveRoom()?.PlayerForSide(targetOwnerSide);
        if (player is null)
        {
            await Clients.Caller.ReceiveActionError("That target is not available.");
            return;
        }
        await RunGameAction(game => game.CastSpell(handIndex, player, targetIndex));
    }

    public async Task AttackPlayer(int attackerIndex)
    {
        var mySide = ResolveSide(out var room);
        if (room is null || mySide is null)
        {
            await Clients.Caller.ReceiveActionError("You are not in an active match.");
            return;
        }
        if (!await RequireActiveSide(room, mySide))
            return;

        var (ok, _, error) = room.DeclarePlayerAttack(attackerIndex);
        if (!ok)
        {
            await Clients.Caller.ReceiveActionError(error ?? "That attack is not allowed.");
            return;
        }

        await BroadcastState(room);
        await MaybeAnnounceWinner(room);
    }

    public async Task BlockAttack(int blockerIndex)
    {
        var mySide = ResolveSide(out var room);
        if (room is null || mySide is null)
        {
            await Clients.Caller.ReceiveActionError("You are not in an active match.");
            return;
        }
        if (!await RequireBlockDecisionSide(room, mySide))
            return;

        var (ok, error) = room.BlockPendingAttack(blockerIndex);
        if (!ok)
        {
            await Clients.Caller.ReceiveActionError(error ?? "You cannot block with that creature.");
            return;
        }

        await BroadcastState(room);
        await MaybeAnnounceWinner(room);
    }

    public async Task PassBlock()
    {
        var mySide = ResolveSide(out var room);
        if (room is null || mySide is null)
        {
            await Clients.Caller.ReceiveActionError("You are not in an active match.");
            return;
        }
        if (!await RequireBlockDecisionSide(room, mySide))
            return;

        var (ok, error) = room.PassPendingAttack();
        if (!ok)
        {
            await Clients.Caller.ReceiveActionError(error ?? "You cannot pass on this attack.");
            return;
        }

        await BroadcastState(room);
        await MaybeAnnounceWinner(room);
    }

    /// <summary>
    /// The caller resigns the current match: their opponent is recorded as
    /// winner through the normal announcement path (broadcast, DuelResult
    /// rows, ELO), exactly like a battlefield defeat.
    /// </summary>
    public async Task Surrender()
    {
        var mySide = ResolveSide(out var room);
        if (room is null || mySide is null)
        {
            await Clients.Caller.ReceiveActionError("You are not in an active match.");
            return;
        }
        if (room.IsBotSide(mySide))
            return;
        if (!room.SurrenderSide(mySide))
        {
            await Clients.Caller.ReceiveActionError("The match has already ended.");
            return;
        }

        await BroadcastState(room);
        await MaybeAnnounceWinner(room);
    }

    public async Task EvolveCreature(int handIndex, int baseIndex)
    {
        var mySide = ResolveSide(out var room);
        if (room is null || mySide is null)
        {
            await Clients.Caller.ReceiveActionError("You are not in an active match.");
            return;
        }
        if (!await RequireActiveSide(room, mySide))
            return;

        // The base creature is always your own battle-zone creature.
        if (!room.Execute(game => game.EvolveCreature(handIndex, baseIndex), out var error))
        {
            await Clients.Caller.ReceiveActionError(error ?? "That evolution is not allowed.");
            return;
        }

        await BroadcastState(room);
        await MaybeAnnounceWinner(room);
    }

    public async Task EvolveCreatureTargeted(int handIndex, int baseIndex, string targetOwnerSide, int targetIndex)
    {
        var mySide = ResolveSide(out var room);
        if (room is null || mySide is null)
        {
            await Clients.Caller.ReceiveActionError("You are not in an active match.");
            return;
        }
        if (!await RequireActiveSide(room, mySide))
            return;

        var player = room.PlayerForSide(targetOwnerSide);
        if (player is null)
        {
            await Clients.Caller.ReceiveActionError("That target is not available.");
            return;
        }

        if (!room.Execute(game => game.EvolveCreature(handIndex, baseIndex, player, targetIndex), out var error))
        {
            await Clients.Caller.ReceiveActionError(error ?? "That evolution is not allowed.");
            return;
        }

        await BroadcastState(room);
        await MaybeAnnounceWinner(room);
    }

    public async Task PlayShieldTrigger(int handIndex)
    {
        var mySide = ResolveSide(out var room);
        if (room is null || mySide is null)
        {
            await Clients.Caller.ReceiveActionError("You are not in an active match.");
            return;
        }
        if (!await RequireShieldTriggerOwner(room, mySide))
            return;

        if (!room.Execute(game => game.PlayShieldTrigger(handIndex), out var error))
        {
            await Clients.Caller.ReceiveActionError(error ?? "You cannot play that card as a Shield Trigger.");
            return;
        }

        await BroadcastState(room);
        await MaybeAnnounceWinner(room);
    }

    public async Task PlayShieldTriggerTargeted(int handIndex, string targetOwnerSide, int targetIndex)
    {
        var mySide = ResolveSide(out var room);
        if (room is null || mySide is null)
        {
            await Clients.Caller.ReceiveActionError("You are not in an active match.");
            return;
        }
        if (!await RequireShieldTriggerOwner(room, mySide))
            return;

        var player = room.PlayerForSide(targetOwnerSide);
        if (player is null)
        {
            await Clients.Caller.ReceiveActionError("That target is not available.");
            return;
        }

        if (!room.Execute(game => game.PlayShieldTrigger(handIndex, player, targetIndex), out var error))
        {
            await Clients.Caller.ReceiveActionError(error ?? "You cannot play that card as a Shield Trigger.");
            return;
        }

        await BroadcastState(room);
        await MaybeAnnounceWinner(room);
    }

    public async Task DeclineShieldTriggers()
    {
        var mySide = ResolveSide(out var room);
        if (room is null || mySide is null)
        {
            await Clients.Caller.ReceiveActionError("You are not in an active match.");
            return;
        }
        if (!await RequireShieldTriggerOwner(room, mySide))
            return;

        room.Execute(game => game.DeclineShieldTriggers(), out _);

        await BroadcastState(room);
        await MaybeAnnounceWinner(room);
    }

    public async Task AttackCreature(int attackerIndex, int targetIndex) =>
        await RunGameAction(room => room.AttackCreature(attackerIndex, targetIndex));

    public async Task ActivateTapAbility(int creatureIndex)
    {
        var mySide = ResolveSide(out var room);
        if (room is null || mySide is null)
        {
            await Clients.Caller.ReceiveActionError("You are not in an active match.");
            return;
        }
        if (!await RequireActiveSide(room, mySide))
            return;

        if (!room.Execute(game => game.ActivateTapAbility(creatureIndex), out var error))
        {
            await Clients.Caller.ReceiveActionError(error ?? "That tap ability cannot be used right now.");
            return;
        }

        await BroadcastState(room);
        await MaybeAnnounceWinner(room);
    }

    public async Task ActivateTapAbilityTargeted(int creatureIndex, string targetSide, int targetIndex)
    {
        var mySide = ResolveSide(out var room);
        if (room is null || mySide is null)
        {
            await Clients.Caller.ReceiveActionError("You are not in an active match.");
            return;
        }
        if (!await RequireActiveSide(room, mySide))
            return;

        var player = room.PlayerForSide(targetSide);
        if (player is null)
        {
            await Clients.Caller.ReceiveActionError("That target is not available.");
            return;
        }

        if (!room.Execute(game => game.ActivateTapAbility(creatureIndex, new[] { new SpellTarget(player, targetIndex) }), out var error))
        {
            await Clients.Caller.ReceiveActionError(error ?? "That tap ability cannot be used right now.");
            return;
        }

        await BroadcastState(room);
        await MaybeAnnounceWinner(room);
    }

    public async Task ActivateTapAbilityRace(int creatureIndex, string race)
    {
        var mySide = ResolveSide(out var room);
        if (room is null || mySide is null)
        {
            await Clients.Caller.ReceiveActionError("You are not in an active match.");
            return;
        }
        if (!await RequireActiveSide(room, mySide))
            return;

        if (!room.Execute(game => game.ActivateTapAbility(creatureIndex, null, race), out var error))
        {
            await Clients.Caller.ReceiveActionError(error ?? "That tap ability cannot be used right now.");
            return;
        }

        await BroadcastState(room);
        await MaybeAnnounceWinner(room);
    }

    /// <summary>
    /// "Choose a shield and look at it" tap ability (e.g. Adomis). The engine taps
    /// the creature and validates everything; the resolved peek - the inspected
    /// shield's card, which stays exactly where it was - is returned to the caller
    /// so only the shield's owner ever sees its face.
    /// </summary>
    public async Task<CardState?> ActivateTapAbilityShield(int creatureIndex, int shieldIndex)
    {
        var mySide = ResolveSide(out var room);
        if (room is null || mySide is null)
        {
            await Clients.Caller.ReceiveActionError("You are not in an active match.");
            return null;
        }
        if (!await RequireActiveSide(room, mySide))
            return null;

        var player = room.PlayerForSide(mySide);
        if (player is null)
        {
            await Clients.Caller.ReceiveActionError("Your player could not be resolved.");
            return null;
        }
        CardState? peeked = null;
        if (!room.Execute(game =>
        {
            game.ActivateTapAbilityShield(creatureIndex, player, shieldIndex);
            peeked = shieldIndex >= 0 && shieldIndex < player.Shields.Count
                ? CardState.FromCard(player.Shields[shieldIndex], $"Shield:{shieldIndex}")
                : null;
        }, out var error))
        {
            await Clients.Caller.ReceiveActionError(error ?? "That shield cannot be looked at right now.");
            return null;
        }

        await BroadcastState(room);
        await MaybeAnnounceWinner(room);
        return peeked;
    }

    /// <summary>
    /// "Look at the top N cards of the deck, then put them back in any order" tap
    /// ability (e.g. Garatyano): tapping pays the cost and opens the scry window.
    /// The exposed top-of-deck cards arrive through the next state broadcast
    /// (<see cref="DuelGameState.ScryCards"/>, owner-visible only), and the player
    /// resolves the window with <see cref="SubmitScryOrder"/>.
    /// </summary>
    public async Task ActivateTapAbilityScry(int creatureIndex)
    {
        var mySide = ResolveSide(out var room);
        if (room is null || mySide is null)
        {
            await Clients.Caller.ReceiveActionError("You are not in an active match.");
            return;
        }
        if (!await RequireActiveSide(room, mySide))
            return;

        var player = room.PlayerForSide(mySide);
        if (player is null)
        {
            await Clients.Caller.ReceiveActionError("Your player could not be resolved.");
            return;
        }
        if (!room.Execute(game => game.ActivateTapAbilityScry(creatureIndex, player), out var error))
        {
            await Clients.Caller.ReceiveActionError(error ?? "That tap ability cannot be used right now.");
            return;
        }

        await BroadcastState(room);
        await MaybeAnnounceWinner(room);
    }

    /// <summary>Put the looked-at deck cards back in the given order (scry window).</summary>
    public async Task SubmitScryOrder(List<string> orderedScryIds)
    {
        var mySide = ResolveSide(out var room);
        if (room is null || mySide is null)
        {
            await Clients.Caller.ReceiveActionError("You are not in an active match.");
            return;
        }
        if (!await RequireActiveSide(room, mySide))
            return;

        var player = room.PlayerForSide(mySide);
        if (!room.Execute(game =>
        {
            var window = game.ScryCards.ToList();
            if (orderedScryIds is null || orderedScryIds.Count != window.Count)
                throw new RuleViolationException("The returned deck order does not match the cards that were looked at.");
            var order = new List<Card>();
            foreach (var token in orderedScryIds)
            {
// Tokens are the "Scry:{i}" instance ids from the state snapshot; the
            // index names the exact position in the exposed window so duplicate
            // catalog cards stay distinguishable.
            var i = ScryTokens.TryGetIndex(token);
            if (i < 0 || i >= window.Count)
                throw new RuleViolationException("The returned deck order does not match the cards that were looked at.");
            order.Add(window[i]);
            }
            game.SubmitScryOrder(order);
        }, out var error))
        {
            await Clients.Caller.ReceiveActionError(error ?? "The deck order could not be submitted right now.");
            return;
        }

        await BroadcastState(room);
        await MaybeAnnounceWinner(room);
    }

    /// <summary>Submit an attack-decision "may look at N shields" answer: shield indices (0-based in the defender's zone).</summary>
    public async Task AttackDecisionAccept(int[] shieldIndices)
    {
        var mySide = ResolveSide(out var room);
        if (room is null || mySide is null)
        {
            await Clients.Caller.ReceiveActionError("You are not in an active match.");
            return;
        }
        if (!await RequireActiveSide(room, mySide))
            return;

        if (!room.Execute(game =>
        {
            var kind = game.PendingAttackDecision;
            if (kind == DuelGame.AttackDecisionKind.LookAtShields)
                game.AcceptAttackLookAtShields(shieldIndices);
            else if (kind == DuelGame.AttackDecisionKind.SearchToHand)
                game.AcceptAttackSearchToHand();
            else
                throw new RuleViolationException("There is no pending attack-decision choice.");
        }, out var error))
        {
            await Clients.Caller.ReceiveActionError(error ?? "That answer is not allowed right now.");
            return;
        }

        await BroadcastState(room);
        await MaybeAnnounceWinner(room);
    }

    /// <summary>Submit an attack-decision "may destroy a creature" answer, naming the target side + battle-zone index.</summary>
    public async Task AttackDecisionAcceptTargeted(string targetSide, int targetIndex)
    {
        var mySide = ResolveSide(out var room);
        if (room is null || mySide is null)
        {
            await Clients.Caller.ReceiveActionError("You are not in an active match.");
            return;
        }
        if (!await RequireActiveSide(room, mySide))
            return;

        var player = room.PlayerForSide(targetSide);
        if (player is null)
        {
            await Clients.Caller.ReceiveActionError("That target is not available.");
            return;
        }

        if (!room.Execute(game => game.AcceptAttackDestroy(player, targetIndex), out var error))
        {
            await Clients.Caller.ReceiveActionError(error ?? "That target is not a legal choice right now.");
            return;
        }

        await BroadcastState(room);
        await MaybeAnnounceWinner(room);
    }

    /// <summary>Decline a pending attack-decision "you may ..." choice; the attack resumes with no effect.</summary>
    public async Task AttackDecisionDecline()
    {
        var mySide = ResolveSide(out var room);
        if (room is null || mySide is null)
        {
            await Clients.Caller.ReceiveActionError("You are not in an active match.");
            return;
        }
        if (!await RequireActiveSide(room, mySide))
            return;

        room.Execute(game => game.DeclineAttackDecision(), out _);

        await BroadcastState(room);
        await MaybeAnnounceWinner(room);
    }

    // -------------------------------------------------------- crew ability

    /// <summary>Activate a Crew ability (no target/race), paying with the creature at <paramref name="payerIndex"/>.</summary>
    public async Task ActivateCrewAbility(int abilityIndex, int payerIndex)
    {
        var mySide = ResolveSide(out var room);
        if (room is null || mySide is null)
        {
            await Clients.Caller.ReceiveActionError("You are not in an active match.");
            return;
        }
        if (!await RequireActiveSide(room, mySide))
            return;

        if (!room.Execute(game => game.ActivateCrewAbility(abilityIndex, payerIndex), out var error))
        {
            await Clients.Caller.ReceiveActionError(error ?? "That Crew ability cannot be used right now.");
            return;
        }

        await BroadcastState(room);
        await MaybeAnnounceWinner(room);
    }

    /// <summary>Activate a Crew ability targeting a creature, paying with the creature at <paramref name="payerIndex"/>.</summary>
    public async Task ActivateCrewAbilityTargeted(int abilityIndex, int payerIndex, string targetSide, int targetIndex)
    {
        var mySide = ResolveSide(out var room);
        if (room is null || mySide is null)
        {
            await Clients.Caller.ReceiveActionError("You are not in an active match.");
            return;
        }
        if (!await RequireActiveSide(room, mySide))
            return;

        var player = room.PlayerForSide(targetSide);
        if (player is null)
        {
            await Clients.Caller.ReceiveActionError("That target is not available.");
            return;
        }

        if (!room.Execute(game => game.ActivateCrewAbility(abilityIndex, payerIndex, new[] { new SpellTarget(player, targetIndex) }), out var error))
        {
            await Clients.Caller.ReceiveActionError(error ?? "That Crew ability cannot be used right now.");
            return;
        }

        await BroadcastState(room);
        await MaybeAnnounceWinner(room);
    }

    /// <summary>Activate a Crew ability naming a race, paying with the creature at <paramref name="payerIndex"/>.</summary>
    public async Task ActivateCrewAbilityRace(int abilityIndex, int payerIndex, string race)
    {
        var mySide = ResolveSide(out var room);
        if (room is null || mySide is null)
        {
            await Clients.Caller.ReceiveActionError("You are not in an active match.");
            return;
        }
        if (!await RequireActiveSide(room, mySide))
            return;

        if (!room.Execute(game => game.ActivateCrewAbility(abilityIndex, payerIndex, null, race), out var error))
        {
            await Clients.Caller.ReceiveActionError(error ?? "That Crew ability cannot be used right now.");
            return;
        }

        await BroadcastState(room);
        await MaybeAnnounceWinner(room);
    }

    public async Task EndMainPhase() => await RunGameAction(room => room.EndMainPhase());

    public async Task EndTurn() => await RunGameAction(room => room.EndTurn());

    // -------------------------------------------------------------- helpers

    private async Task<bool> RequireActiveSide(MatchRoom room, string mySide)
    {
        var activeSide = room.ActiveSide();
        if (activeSide is null || !string.Equals(mySide, activeSide, StringComparison.Ordinal))
        {
            await Clients.Caller.ReceiveActionError("It is not your turn.");
            return false;
        }
        return true;
    }

    private async Task<bool> RequireBlockDecisionSide(MatchRoom room, string mySide)
    {
        if (!room.HasPendingBlock)
        {
            await Clients.Caller.ReceiveActionError("There is no attack awaiting a blocker.");
            return false;
        }
        var decisionSide = room.BlockDecisionSide();
        if (decisionSide is null || !string.Equals(mySide, decisionSide, StringComparison.Ordinal))
        {
            await Clients.Caller.ReceiveActionError("You may not choose a blocker right now.");
            return false;
        }
        return true;
    }

    private async Task<bool> RequireShieldTriggerOwner(MatchRoom room, string mySide)
    {
        if (!room.IsShieldTriggerOwnerSide(mySide))
        {
            await Clients.Caller.ReceiveActionError("You have no Shield Trigger cards to play right now.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Resolve a saved deck id into its domain cards (40 cards, validated). Returns
    /// null when the deck does not exist or no longer validates, so the caller falls
    /// back to a random deck.
    /// </summary>
    private List<Card>? LoadDeckById(Guid deckId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var deck = db.Decks
            .Include(d => d.Cards)
            .SingleOrDefault(d => d.Id == deckId);
        if (deck is null)
            return null;
        var (ok, _, cards) = MatchCardCatalog.BuildDeck(deck.Cards.Select(c => (c.CardId, c.Count)));
        return ok ? cards : null;
    }

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

        if (room.HasPendingBlock)
        {
            await Clients.Caller.ReceiveActionError("An attack is awaiting a blocking decision.");
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

    private Guid? CurrentUserIdOrNull() =>
        Guid.TryParse(Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

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
            if (room.IsBotSide(side))
                continue;
            var connectionId = room.SideConnections[side];
            await Clients.Client(connectionId).ReceiveGameState(room.StateFor(side));
        }

        // A bot seat can own the next decision (its whole new turn, a blocker
        // choice against the human's attack, its own shield triggers). Drive it
        // until the ball is back with a human; each applied change re-broadcasts.
        await DriveBotIfNeeded(room);
    }

    /// <summary>
    /// Let the room's server-side AI apply everything currently owed to it. When it
    /// made progress the new board is broadcast again (which re-enters here) until
    /// the game is waiting on a human seat or a winner is announced.
    /// </summary>
    private async Task DriveBotIfNeeded(MatchRoom room)
    {
        var bot = room.Bot;
        if (bot is null)
            return;
        if (bot.Drive() <= 0)
            return;
        await BroadcastState(room);
        await MaybeAnnounceWinner(room);
    }

    private async Task MaybeAnnounceWinner(MatchRoom room)
    {
        var winner = room.WinnerSide;
        if (winner is null)
            return;
        foreach (var (side, connectionId) in room.SideConnections)
        {
            if (room.IsBotSide(side))
                continue;
            await Clients.Client(connectionId).AnnounceWinner(winner);
        }
        await RecordDuelResultsOnceAsync(room, winner);
        await CloseMatchRecordAsync(room.Code, winner);
        // Keep the room around after a win: the seated players may ask for a rematch
        // or a reconnect away and come back. Finished matches are swept once both
        // human seats disconnect (OnDisconnectedAsync).
    }

    /// <summary>
    /// Persist one <see cref="DuelResult"/> row per deck-owning side, exactly once
    /// per round. Sides are attributed through their saved deck's owner; bot seats
    /// and sides without a saved deck are skipped.
    /// </summary>
    private async Task RecordDuelResultsOnceAsync(MatchRoom room, string winnerSide)
    {
        if (!room.TryMarkDuelResultsRecorded())
            return;
        var playedAt = DateTime.UtcNow;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owners = new Dictionary<string, Guid>();
        foreach (var side in new[] { DuelSide.Player1, DuelSide.Player2 })
        {
            if (room.IsBotSide(side))
                continue;
            var deckId = side == DuelSide.Player1 ? room.HostDeckId : room.JoinerDeckId;
            if (deckId is not { } id)
                continue;
            var ownerId = await db.Decks
                .Where(d => d.Id == id)
                .Select(d => d.UserId)
                .SingleOrDefaultAsync();
            if (ownerId == Guid.Empty)
                continue;
            // A side is ranked only for its own deck: a seat piloting someone
            // else's saved deck (or no authenticated seat at all) keeps the
            // game result but earns no history row and no rating movement.
            if (room.SideUser(side) is not { } seatUser || seatUser != ownerId)
                continue;
            owners[side] = ownerId;
            db.DuelResults.Add(new DuelResult
            {
                UserId = ownerId,
                MatchCode = room.Code,
                Won = string.Equals(side, winnerSide, StringComparison.Ordinal),
                DeckId = id,
                PlayedAtUtc = playedAt,
            });
        }
        // Ranked movement happens only for human-vs-human games where both
        // sides resolved to a saved-deck owner. Vs-AI and deck-less sides keep
        // their DuelResult history rows but no rating change.
        if (owners.TryGetValue(DuelSide.Player1, out var p1Id)
            && owners.TryGetValue(DuelSide.Player2, out var p2Id))
        {
            var profiles = await db.PlayerProfiles
                .Where(p => p.UserId == p1Id || p.UserId == p2Id)
                .ToDictionaryAsync(p => p.UserId);
            foreach (var id in new[] { p1Id, p2Id })
            {
                if (!profiles.ContainsKey(id))
                {
                    var created = new PlayerProfile
                    {
                        UserId = id,
                        Nickname = "Player-" + id.ToString("N"),
                    };
                    db.PlayerProfiles.Add(created);
                    profiles[id] = created;
                }
            }
            var p1 = profiles[p1Id];
            var p2 = profiles[p2Id];
            var p1Won = string.Equals(DuelSide.Player1, winnerSide, StringComparison.Ordinal);
            if (p1Won) { p1.OnlineWins++; p2.OnlineLosses++; }
            else { p2.OnlineWins++; p1.OnlineLosses++; }
            var (winnerRating, loserRating) = p1Won
                ? EloRating.Apply(p1.Rating, p2.Rating)
                : EloRating.Apply(p2.Rating, p1.Rating);
            if (p1Won) { p1.Rating = winnerRating; p2.Rating = loserRating; }
            else { p2.Rating = winnerRating; p1.Rating = loserRating; }
            p1.UpdatedAtUtc = playedAt;
            p2.UpdatedAtUtc = playedAt;
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Open the persistent registry row when a match is hosted. The row
    /// survives process restarts, unlike the in-memory room.
    /// </summary>
    private async Task OpenMatchRecordAsync(string code, Guid? hostUserId, string hostName, Guid? hostDeckId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.MatchRecords.Add(new MatchRecord
        {
            Code = code,
            HostUserId = hostUserId,
            HostName = hostName,
            HostDeckId = hostDeckId,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Fill in the joiner side once the second seat is taken.</summary>
    private async Task FillJoinerAsync(string code, Guid? joinerUserId, string joinerName, Guid? joinerDeckId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await db.MatchRecords.SingleOrDefaultAsync(m => m.Code == code);
        if (record is null)
            return;
        record.JoinerUserId = joinerUserId;
        record.JoinerName = joinerName;
        record.JoinerDeckId = joinerDeckId;
        record.IsRanked = record.HostUserId.HasValue && joinerUserId.HasValue;
        await db.SaveChangesAsync();
    }

    /// <summary>Close the registry row at winner announcement. Idempotent:
    /// only the first close stamps the finish.</summary>
    private async Task CloseMatchRecordAsync(string code, string winnerSide)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await db.MatchRecords.SingleOrDefaultAsync(m => m.Code == code);
        if (record is null || record.FinishedAtUtc is not null)
            return;
        record.FinishedAtUtc = DateTime.UtcNow;
        record.WinnerSide = winnerSide;
        await db.SaveChangesAsync();
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
