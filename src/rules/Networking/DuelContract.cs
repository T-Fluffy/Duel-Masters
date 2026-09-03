namespace DuelMasters.Domain.Networking;

/// <summary>
/// Shared names for the SignalR hub methods and hub-streamed/client-received events.
/// Kept in the domain so the server hub and the Godot client reference identical
/// strings, avoiding silent wire-protocol drift.
/// </summary>
public static class DuelContract
{
    /// <summary>Hub methods the client <c>InvokeAsync</c>s on the duel endpoint.</summary>
    public static class Hub
    {
        public const string HostMatch = "HostMatch";
        public const string JoinMatch = "JoinMatch";
        public const string StartTurn = "StartTurn";
        public const string Draw = "Draw";
        public const string PlayMana = "PlayMana";
        public const string SummonCreature = "SummonCreature";
        public const string CastSpell = "CastSpell";
        public const string AttackPlayer = "AttackPlayer";
        public const string AttackCreature = "AttackCreature";
        public const string EndMainPhase = "EndMainPhase";
        public const string EndTurn = "EndTurn";
    }

    /// <summary>Events the server pushes to clients via <c>IDuelClientContract</c>.</summary>
    public static class Client
    {
        public const string ReceiveGameState = "ReceiveGameState";
        public const string ReceiveActionError = "ReceiveActionError";
        public const string AnnounceWinner = "AnnounceWinner";
        public const string MatchJoined = "MatchJoined";
    }
}
