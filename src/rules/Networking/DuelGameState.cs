using System.Collections.Generic;
using System.Text.Json.Serialization;
using DuelMasters.Domain;

namespace DuelMasters.Domain.Networking;

/// <summary>
/// The authoritative, viewer-relative snapshot of a networked match. A <c>DuelGame</c>
/// runs entirely on the server; every client receives this state for its own side,
/// with the opponent's hidden information already redacted server-side.
/// </summary>
public sealed class DuelGameState
{
    [JsonPropertyName("matchCode")]
    public string MatchCode { get; set; } = "";

    /// <summary>The viewer's assigned side (used to decide which hand to reveal).</summary>
    [JsonPropertyName("yourSide")]
    public string YourSide { get; set; } = DuelSide.Player1;

    [JsonPropertyName("activeSide")]
    public string ActiveSide { get; set; } = DuelSide.Player1;

    [JsonPropertyName("phase")]
    public string Phase { get; set; } = "";

    [JsonPropertyName("turnNumber")]
    public int TurnNumber { get; set; }

    [JsonPropertyName("yourTurn")]
    public bool YourTurn { get; set; }

    [JsonPropertyName("canPlayMana")]
    public bool CanPlayMana { get; set; }

    [JsonPropertyName("canSummonOrCast")]
    public bool CanSummonOrCast { get; set; }

    [JsonPropertyName("canAttack")]
    public bool CanAttack { get; set; }

    [JsonPropertyName("shieldTriggerWindowActive")]
    public bool ShieldTriggerWindowActive { get; set; }

    /// <summary>Side that owns the pending Shield Trigger cards ("Player1"/"Player2"), when a window is open.</summary>
    [JsonPropertyName("shieldTriggerOwnerSide")]
    public string? ShieldTriggerOwnerSide { get; set; }

    /// <summary>Hand indices (into the trigger owner's hand) of the pending Shield Trigger cards. Only visible to that owner.</summary>
    [JsonPropertyName("pendingTriggerHandIndices")]
    public List<int> PendingTriggerHandIndices { get; set; } = new();

    /// <summary>True while an attack is declared and the defender may choose to block or pass.</summary>
    [JsonPropertyName("attackPending")]
    public bool AttackPending { get; set; }

    /// <summary>The battling attacker's battle-zone index, while <see cref="AttackPending"/> is true.</summary>
    [JsonPropertyName("attackPendingAttackerIndex")]
    public int AttackPendingAttackerIndex { get; set; }

    /// <summary>Number of ready Blocker creatures the defender may choose, while <see cref="AttackPending"/> is true.</summary>
    [JsonPropertyName("blocksAvailable")]
    public int BlocksAvailable { get; set; }

    [JsonPropertyName("isGameOver")]
    public bool IsGameOver { get; set; }

    [JsonPropertyName("winnerId")]
    public string? WinnerId { get; set; }

    [JsonPropertyName("players")]
    public List<PlayerState> Players { get; set; } = new();

    /// <summary>Build the viewer's state from an authoritative engine + which side they are.</summary>
    public static DuelGameState From(DuelGame game, string matchCode, string viewerSide)
    {
        var activeSide = DuelSide.FromIndex(game.ActivePlayer == game.Player1 ? 0 : 1);
        var phase = game.Phase.ToString();
        var yourTurn = activeSide == viewerSide;
        var canPlayMana = yourTurn && game.Phase == GamePhase.Main && !game.ManaChargedThisTurn && !game.IsGameOver;
        var canSummonOrCast = yourTurn && game.Phase == GamePhase.Main && !game.HasAttackedThisTurn && !game.IsGameOver;
        var canAttack = yourTurn && game.Phase == GamePhase.Main && !game.HasAttackedThisTurn && !game.IsGameOver;

        var p1 = DuelSide.FromIndex(0);
        var p2 = DuelSide.FromIndex(1);

        var triggerOwner = game.ShieldTriggerOwner;
        var triggerSide = triggerOwner is null ? null : DuelSide.FromIndex(triggerOwner == game.Player1 ? 0 : 1);
        var pendingIndices = new List<int>();
        if (triggerOwner is not null && triggerSide == viewerSide)
        {
            foreach (var pending in game.PendingShieldTriggers)
            {
                var handIndex = triggerOwner.Hand.IndexOf(pending);
                if (handIndex >= 0)
                    pendingIndices.Add(handIndex);
            }
        }

        return new DuelGameState
        {
            MatchCode = matchCode,
            YourSide = viewerSide,
            ActiveSide = activeSide,
            Phase = phase,
            TurnNumber = game.TurnNumber,
            YourTurn = yourTurn,
            CanPlayMana = canPlayMana,
            CanSummonOrCast = canSummonOrCast,
            CanAttack = canAttack,
            ShieldTriggerWindowActive = game.ShieldTriggerWindowActive,
            ShieldTriggerOwnerSide = triggerSide,
            PendingTriggerHandIndices = pendingIndices,
            IsGameOver = game.IsGameOver,
            WinnerId = game.Winner is null ? null : DuelSide.FromIndex(game.Winner == game.Player1 ? 0 : 1),
            Players =
            {
                PlayerState.From(game.Player1, p1, viewerSide),
                PlayerState.From(game.Player2, p2, viewerSide),
            },
        };
    }
}
