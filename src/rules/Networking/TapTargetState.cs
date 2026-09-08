using System.Text.Json.Serialization;

namespace DuelMasters.Domain.Networking;

/// <summary>
/// One pre-validated legal target for a creature's Tap Ability, computed by the
/// authoritative engine on the server for the viewer's own creatures. The zone is
/// implied by the ability's <c>EffectTargetScope</c>; the index is the engine-zone
/// position inside that zone.
/// </summary>
public sealed class TapTargetState
{
    /// <summary>Which player owns the target card ("Player1"/"Player2").</summary>
    [JsonPropertyName("side")]
    public string Side { get; set; } = DuelSide.Player1;

    /// <summary>The engine-zone index (of the battle zone, mana zone, or graveyard
    /// the ability targets).</summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>Human-readable row label, e.g. "Your creature - Burning Blade (3000)".</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    public TapTargetState() { }

    public TapTargetState(string side, int index, string label)
    {
        Side = side;
        Index = index;
        Label = label;
    }
}