using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using DuelMasters.Domain.Networking;
using Microsoft.AspNetCore.SignalR.Client;

namespace DuelMasters.Networking;

/// <summary>
/// Static SignalR transport for the networked duel. Owns a single
/// <see cref="HubConnection"/> to the authoritative hub and exposes high-level
/// host/join/action calls. Incoming pushes are pushed onto thread-safe queues and
/// consumed by the UI on the main thread via the <c>TryDequeue*</c> methods.
/// </summary>
public static class NetworkClient
{
    public const string DefaultServerUrl = "http://127.0.0.1:8080/duel";

    private static HubConnection? _connection;
    private static readonly ConcurrentQueue<DuelGameState> StateQueue = new();
    private static readonly ConcurrentQueue<string> ErrorQueue = new();
    private static readonly ConcurrentQueue<string> WinnerQueue = new();
    private static readonly ConcurrentQueue<MatchInfo> JoinedQueue = new();
    private static readonly ConcurrentQueue<CardState> PeekQueue = new();

    public static bool IsConnected { get; private set; }

    /// <summary>The match code for the live connection, if any.</summary>
    public static string? MatchCode { get; private set; }

    /// <summary>The side this client was assigned ("Player1"/"Player2"), if any.</summary>
    public static string? YourSide { get; private set; }

    /// <summary>The most recent viewer-relative state, for convenience.</summary>
    public static DuelGameState? CurrentState { get; private set; }

    /// <summary>True once a winner has been announced for the current match.</summary>
    public static bool MatchEnded { get; private set; }

    // ------------------------------------------------------------- lifecycle

    public static async Task ConnectAsync(string url)
    {
        if (_connection is { State: HubConnectionState.Connected })
            return;

        var connection = new HubConnectionBuilder()
            .WithUrl(string.IsNullOrWhiteSpace(url) ? DefaultServerUrl : url)
            .WithAutomaticReconnect()
            .Build();

        connection.On<DuelGameState>(DuelContract.Client.ReceiveGameState, state =>
        {
            CurrentState = state;
            StateQueue.Enqueue(state);
        });
        connection.On<string>(DuelContract.Client.ReceiveActionError, error => ErrorQueue.Enqueue(error));
        connection.On<string>(DuelContract.Client.AnnounceWinner, winner =>
        {
            MatchEnded = true;
            WinnerQueue.Enqueue(winner);
        });
        connection.On<MatchInfo>(DuelContract.Client.MatchJoined, info =>
        {
            MatchCode = info.MatchCode;
            YourSide = info.YourSide;
            JoinedQueue.Enqueue(info);
        });

        connection.Closed += _ =>
        {
            IsConnected = false;
            return Task.CompletedTask;
        };

        _connection = connection;
        await connection.StartAsync();
        IsConnected = true;
    }

    public static async Task DisconnectAsync()
    {
        if (_connection is { State: HubConnectionState.Connected })
            await _connection.StopAsync();
        _connection = null;
        IsConnected = false;
        MatchCode = null;
        YourSide = null;
        MatchEnded = false;
        CurrentState = null;
    }

    // ------------------------------------------------------------- actions

    public static void HostMatch(string name, Guid? deckId = null) =>
        FireAndForget(async () =>
        {
            var info = await _connection!.InvokeAsync<MatchInfo>(DuelContract.Hub.HostMatch, name, deckId);
            MatchCode = info.MatchCode;
            YourSide = info.YourSide;
            JoinedQueue.Enqueue(info);
        });

    public static void JoinMatch(string code, string name, Guid? deckId = null) =>
        FireAndForget(async () =>
        {
            var info = await _connection!.InvokeAsync<MatchInfo>(DuelContract.Hub.JoinMatch, code, name, deckId);
            MatchCode = info.MatchCode;
            YourSide = info.YourSide;
            JoinedQueue.Enqueue(info);
        });

    public static void StartTurn() => Invoke(DuelContract.Hub.StartTurn);
    public static void Draw() => Invoke(DuelContract.Hub.Draw);
    public static void PlayMana(int handIndex) => Invoke(DuelContract.Hub.PlayMana, handIndex);
    public static void SummonCreature(int handIndex) => Invoke(DuelContract.Hub.SummonCreature, handIndex);
    public static void SummonCreatureTargeted(int handIndex, string targetOwnerSide, int targetIndex) =>
        Invoke(DuelContract.Hub.SummonCreatureTargeted, handIndex, targetOwnerSide, targetIndex);
    public static void CastSpell(int handIndex) => Invoke(DuelContract.Hub.CastSpell, handIndex);
    public static void CastSpellTargeted(int handIndex, string targetOwnerSide, int targetIndex) =>
        Invoke(DuelContract.Hub.CastSpellTargeted, handIndex, targetOwnerSide, targetIndex);
    public static void AttackPlayer(int attackerIndex) => Invoke(DuelContract.Hub.AttackPlayer, attackerIndex);
    public static void AttackCreature(int attackerIndex, int targetIndex) =>
        Invoke(DuelContract.Hub.AttackCreature, attackerIndex, targetIndex);
    public static void BlockAttack(int blockerIndex) => Invoke(DuelContract.Hub.BlockAttack, blockerIndex);
    public static void PassBlock() => Invoke(DuelContract.Hub.PassBlock);
    public static void EvolveCreature(int handIndex, int baseIndex) =>
        Invoke(DuelContract.Hub.EvolveCreature, handIndex, baseIndex);
    public static void EvolveCreatureTargeted(int handIndex, int baseIndex, string targetOwnerSide, int targetIndex) =>
        Invoke(DuelContract.Hub.EvolveCreatureTargeted, handIndex, baseIndex, targetOwnerSide, targetIndex);
    public static void PlayShieldTrigger(int handIndex) => Invoke(DuelContract.Hub.PlayShieldTrigger, handIndex);
    public static void PlayShieldTriggerTargeted(int handIndex, string targetOwnerSide, int targetIndex) =>
        Invoke(DuelContract.Hub.PlayShieldTriggerTargeted, handIndex, targetOwnerSide, targetIndex);
    public static void DeclineShieldTriggers() => Invoke(DuelContract.Hub.DeclineShieldTriggers);
    public static void ActivateTapAbility(int creatureIndex) =>
        Invoke(DuelContract.Hub.ActivateTapAbility, creatureIndex);
    public static void ActivateTapAbilityTargeted(int creatureIndex, string targetSide, int targetIndex) =>
        Invoke(DuelContract.Hub.ActivateTapAbilityTargeted, creatureIndex, targetSide, targetIndex);

    public static void ActivateTapAbilityRace(int creatureIndex, string race) =>
        Invoke(DuelContract.Hub.ActivateTapAbilityRace, creatureIndex, race);
    public static void EndMainPhase() => Invoke(DuelContract.Hub.EndMainPhase);
    public static void EndTurn() => Invoke(DuelContract.Hub.EndTurn);

    /// <summary>
    /// Tap ability "choose a shield and look at it": the server taps the creature,
    /// validates the shield, and returns the inspected card to the caller only.
    /// </summary>
    public static void ActivateTapAbilityShield(int creatureIndex, int shieldIndex)
    {
        FireAndForget(async () =>
        {
            var peeked = await _connection!.InvokeAsync<CardState?>(DuelContract.Hub.ActivateTapAbilityShield, creatureIndex, shieldIndex);
            if (peeked is not null)
                PeekQueue.Enqueue(peeked);
        });
    }

    /// <summary>Tap ability "look at the top N cards of your deck": opens the scry window.</summary>
    public static void ActivateTapAbilityScry(int creatureIndex) =>
        Invoke(DuelContract.Hub.ActivateTapAbilityScry, creatureIndex);

    /// <summary>Put the looked-at deck cards back in the given order (top-to-bottom).</summary>
    public static void SubmitScryOrder(List<string> orderedScryIds) =>
        Invoke(DuelContract.Hub.SubmitScryOrder, orderedScryIds);

    // ------------------------------------------------------------- polling

    public static bool TryDequeueState(out DuelGameState state) => StateQueue.TryDequeue(out state!);
    public static bool TryDequeueError(out string error) => ErrorQueue.TryDequeue(out error!);
    public static bool TryDequeueWinner(out string winner) => WinnerQueue.TryDequeue(out winner!);
    public static bool TryDequeueJoined(out MatchInfo info) => JoinedQueue.TryDequeue(out info!);
    public static bool TryDequeuePeeked(out CardState state) => PeekQueue.TryDequeue(out state!);

    // ------------------------------------------------------------- helpers

    private static void Invoke(string method, params object?[] args) =>
        FireAndForget(async () => await _connection!.InvokeCoreAsync(method, args));

    private static async void FireAndForget(Func<Task> op)
    {
        try
        {
            await op();
        }
        catch (Exception ex)
        {
            ErrorQueue.Enqueue($"Network error: {ex.Message}");
        }
    }
}
