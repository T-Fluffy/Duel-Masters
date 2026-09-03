using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using DuelMasters.Domain;

namespace DuelMasters.Domain.Networking;

/// <summary>One player's snapshot within a <see cref="DuelGameState"/>.</summary>
public sealed class PlayerState
{
    [JsonPropertyName("side")]
    public string Side { get; set; } = DuelSide.Player1;

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>The player's hand. Real cards are only included for the owner;
    /// opponents receive a single face-down entry per card instead.</summary>
    [JsonPropertyName("hand")]
    public List<CardState> Hand { get; set; } = new();

    [JsonPropertyName("handCount")]
    public int HandCount { get; set; }

    [JsonPropertyName("manaZone")]
    public List<CardState> ManaZone { get; set; } = new();

    [JsonPropertyName("battleZone")]
    public List<CardState> BattleZone { get; set; } = new();

    [JsonPropertyName("graveyardCount")]
    public int GraveyardCount { get; set; }

    [JsonPropertyName("shieldCount")]
    public int ShieldCount { get; set; }

    [JsonPropertyName("deckCount")]
    public int DeckCount { get; set; }

    /// <summary>Total untapped mana available (used by the client for affordability hints).</summary>
    [JsonPropertyName("untappedMana")]
    public int UntappedMana { get; set; }

    /// <summary>Snapshot the given player from the engine's perspective.
    /// <paramref name="viewerSide"/> controls what hidden info is exposed.</summary>
    public static PlayerState From(Player player, string side, string viewerSide)
    {
        var state = new PlayerState
        {
            Side = side,
            Name = player.Name,
            HandCount = player.Hand.Count,
            GraveyardCount = player.Graveyard.Count,
            ShieldCount = player.ShieldCount,
            DeckCount = player.Deck.Count,
            UntappedMana = player.UntappedMana.Count(),
        };

        var revealHand = side == viewerSide;
        for (var i = 0; i < player.Hand.Count; i++)
        {
            state.Hand.Add(revealHand
                ? CardState.FromInstance(player.Hand[i], BuildId(side, "H", i))
                : CardState.FaceDown(BuildId(side, "H", i)));
        }

        for (var i = 0; i < player.ManaZone.Count; i++)
            state.ManaZone.Add(CardState.FromInstance(player.ManaZone[i], BuildId(side, "M", i)));

        for (var i = 0; i < player.BattleZone.Count; i++)
            state.BattleZone.Add(CardState.FromInstance(player.BattleZone[i], BuildId(side, "B", i)));

        return state;
    }

    private static string BuildId(string side, string zone, int index) => $"{side}:{zone}:{index}";
}
