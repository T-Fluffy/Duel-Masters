using System.Text.Json.Serialization;

namespace DuelMasters.Domain.Networking;

/// <summary>
/// Returned to a client after it hosts or joins a networked match. Carries the
/// match code plus which side the caller was assigned (see <see cref="DuelSide"/>).
/// </summary>
public sealed class MatchInfo
{
    /// <summary>The short code participants use to join the match.</summary>
    [JsonPropertyName("matchCode")]
    public string MatchCode { get; set; } = "";

    /// <summary>The side the caller was assigned: "Player1" or "Player2".</summary>
    [JsonPropertyName("yourSide")]
    public string YourSide { get; set; } = DuelSide.Player1;

    /// <summary>The caller's display name.</summary>
    [JsonPropertyName("yourName")]
    public string YourName { get; set; } = "";

    /// <summary>The other participant's display name (may be empty until both joined).</summary>
    [JsonPropertyName("opponentName")]
    public string OpponentName { get; set; } = "";
}

/// <summary>The two fixed sides of a <c>DuelGame</c>, mapped directly to Player1/Player2.</summary>
public static class DuelSide
{
    public const string Player1 = "Player1";
    public const string Player2 = "Player2";

    public static string FromIndex(int index) => index == 0 ? Player1 : Player2;

    public static int ToIndex(string side) => side == Player2 ? 1 : 0;
}
