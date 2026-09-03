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
