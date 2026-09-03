using System.Text.Json.Serialization;
using DuelMasters.Domain;

namespace DuelMasters.Domain.Networking;

/// <summary>
/// A single card rendered on the board, as seen by a specific player. Hidden
/// information (the opponent's hand, shield contents) is never serialized; the
/// server instead sends just <see cref="CountOnly"/> for those zones.
/// </summary>
public sealed class CardState
{
    /// <summary>A stable, unique id within this match (e.g. "P1:H:0").</summary>
    [JsonPropertyName("instanceId")]
    public string InstanceId { get; set; } = "";

    /// <summary>The catalog id of the underlying card.</summary>
    [JsonPropertyName("cardId")]
    public string CardId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("civilization")]
    public string Civilization { get; set; } = "";

    [JsonPropertyName("cardType")]
    public string CardType { get; set; } = "";

    [JsonPropertyName("manaCost")]
    public int ManaCost { get; set; }

    [JsonPropertyName("power")]
    public int Power { get; set; }

    [JsonPropertyName("isTapped")]
    public bool IsTapped { get; set; }

    [JsonPropertyName("isSummoningSick")]
    public bool IsSummoningSick { get; set; }

    /// <summary>True when this entry only represents a face-down card (no details).</summary>
    [JsonPropertyName("countOnly")]
    public bool CountOnly { get; set; }

    public static CardState FaceDown(string instanceId, int power = 0) => new()
    {
        InstanceId = instanceId,
        CountOnly = true,
        Power = power,
    };

    public static CardState FromInstance(CardInstance instance, string instanceId)
    {
        var card = instance.Card;
        return new CardState
        {
            InstanceId = instanceId,
            CardId = card.Id,
            Name = card.Name,
            Civilization = card.Civilization.ToString(),
            CardType = card.CardType.ToString(),
            ManaCost = card.ManaCost,
            Power = card.Power,
            IsTapped = instance.IsTapped,
            IsSummoningSick = instance.IsSummoningSick,
        };
    }
}
