using System;
using System.Collections.Concurrent;
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

    public static void HostMatch(string name) =>
        FireAndForget(async () =>
        {
            var info = await _connection!.InvokeAsync<MatchInfo>(DuelContract.Hub.HostMatch, name);
            MatchCode = info.MatchCode;
            YourSide = info.YourSide;
            JoinedQueue.Enqueue(info);
        });

    public static void JoinMatch(string code, string name) =>
        FireAndForget(async () =>
        {
            var info = await _connection!.InvokeAsync<MatchInfo>(DuelContract.Hub.JoinMatch, code, name);
            MatchCode = info.MatchCode;
            YourSide = info.YourSide;
            JoinedQueue.Enqueue(info);
        });

    public static void StartTurn() => Invoke(DuelContract.Hub.StartTurn);
    public static void Draw() => Invoke(DuelContract.Hub.Draw);
    public static void PlayMana(int handIndex) => Invoke(DuelContract.Hub.PlayMana, handIndex);
    public static void SummonCreature(int handIndex) => Invoke(DuelContract.Hub.SummonCreature, handIndex);
    public static void CastSpell(int handIndex) => Invoke(DuelContract.Hub.CastSpell, handIndex);
    public static void AttackPlayer(int attackerIndex) => Invoke(DuelContract.Hub.AttackPlayer, attackerIndex);
    public static void AttackCreature(int attackerIndex, int targetIndex) =>
        Invoke(DuelContract.Hub.AttackCreature, attackerIndex, targetIndex);
    public static void EndMainPhase() => Invoke(DuelContract.Hub.EndMainPhase);
    public static void EndTurn() => Invoke(DuelContract.Hub.EndTurn);

    // ------------------------------------------------------------- polling

    public static bool TryDequeueState(out DuelGameState state) => StateQueue.TryDequeue(out state!);
    public static bool TryDequeueError(out string error) => ErrorQueue.TryDequeue(out error!);
    public static bool TryDequeueWinner(out string winner) => WinnerQueue.TryDequeue(out winner!);
    public static bool TryDequeueJoined(out MatchInfo info) => JoinedQueue.TryDequeue(out info!);

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
