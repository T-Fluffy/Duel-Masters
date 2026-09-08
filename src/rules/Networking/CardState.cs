using System.Collections.Generic;
using System.Linq;
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

    [JsonPropertyName("race")]
    public string Race { get; set; } = "";

    [JsonPropertyName("keywords")]
    public List<string> Keywords { get; set; } = new();

    /// <summary>True when this entry is an Evolution creature.</summary>
    [JsonPropertyName("isEvolution")]
    public bool IsEvolution { get; set; }

    /// <summary>The race an Evolution creature must be placed on (empty otherwise).</summary>
    [JsonPropertyName("evolutionOf")]
    public string EvolutionOf { get; set; } = "";

    [JsonPropertyName("isTapped")]
    public bool IsTapped { get; set; }

    [JsonPropertyName("isSummoningSick")]
    public bool IsSummoningSick { get; set; }

    /// <summary>True when the underlying card carries a Tap Ability.</summary>
    [JsonPropertyName("hasTapAbility")]
    public bool HasTapAbility { get; set; }

    /// <summary>Server-computed, only set for the viewer's own creatures: the viewer
    /// may activate one of this creature's tap abilities right now.</summary>
    [JsonPropertyName("canUseTapAbility")]
    public bool CanUseTapAbility { get; set; }

    /// <summary>Server-computed legal Tap-Ability targets for the viewer (null/empty
    /// when the ability resolves globally without a target choice).</summary>
    [JsonPropertyName("tapAbilityTargets")]
    public List<TapTargetState>? TapAbilityTargets { get; set; }

    /// <summary>Server-computed "choose a race" pool for a Tap Ability that needs a
    /// race choice (non-empty only while the viewer's creature can use such an
    /// ability; it lists the distinct races currently in either battle zone).</summary>
    [JsonPropertyName("tapAbilityRaces")]
    public List<string>? TapAbilityRaces { get; set; }

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
            Race = card.Race,
            Keywords = card.Keywords.Select(k => k.ToString()).OrderBy(k => k).ToList(),
            IsEvolution = card.IsEvolution,
            EvolutionOf = card.EvolutionOf,
            IsTapped = instance.IsTapped,
            IsSummoningSick = instance.IsSummoningSick,
        };
    }
}
