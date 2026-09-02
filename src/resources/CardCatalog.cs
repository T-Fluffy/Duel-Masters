using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DuelMasters.Domain;
using Godot;

namespace DuelMasters.Resources;

/// <summary>
/// Loads the Phase 1 card catalog (<c>res://src/resources/data/cards.json</c>)
/// into the shared domain <see cref="Card"/> model plus the client-side metadata
/// (artwork path, effect id) that the visual arena needs.
///
/// The domain card is the single source of truth for rules; the client-only fields
/// live in the lightweight <see cref="CardRecord"/> wrapper returned alongside it.
/// </summary>
public static class CardCatalog
{
    public const string CardsJsonPath = "res://src/resources/data/cards.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// A catalog entry: the shared domain card plus client-side metadata.
    /// </summary>
    public sealed record CardRecord(Card Card, string ImagePath, string ScriptEffectId);

    /// <summary>
    /// A parsed card as stored in cards.json, before mapping to the domain model.
    /// </summary>
    private sealed class CardJson
    {
        public string Id { get; set; } = "";
        public string? Name { get; set; }
        public string? Civilization { get; set; }
        public string CardType { get; set; } = "Creature";
        public int ManaCost { get; set; }
        public int? Power { get; set; }
        public string Race { get; set; } = "";
        public string ImagePath { get; set; } = "";
        public List<string> Keywords { get; set; } = new();
        public string ScriptEffectId { get; set; } = "";
    }

    /// <summary>All named cards in the catalog, sorted by name.</summary>
    public static List<CardRecord> Load(IReadOnlyList<string>? ids = null)
    {
        if (!FileAccess.FileExists(CardsJsonPath))
            throw new InvalidOperationException($"Card catalog not found at {CardsJsonPath}.");

        using var file = FileAccess.Open(CardsJsonPath, FileAccess.ModeFlags.Read);
        var text = file.GetAsText();
        var list = JsonSerializer.Deserialize<List<CardJson>>(text, JsonOptions) ?? new List<CardJson>();

        var wanted = ids is not null ? new HashSet<string>(ids) : null;
        var records = new List<CardRecord>(list.Count);
        foreach (var c in list)
        {
            if (string.IsNullOrEmpty(c.Name) || string.IsNullOrEmpty(c.Civilization))
                continue; // DMR-23-Promo skeleton cards have no name/civ; skip.
            if (wanted is not null && !wanted.Contains(c.Id))
                continue;

            var civ = ParseCivilization(c.Civilization!);
            var type = ParseCardType(c.CardType);
            var keywords = ParseKeywords(c.Keywords);

            var card = new Card(c.Id, c.Name!, civ, type, c.ManaCost, c.Power ?? 0, c.Race ?? "", keywords);
            records.Add(new CardRecord(card, c.ImagePath, c.ScriptEffectId));
        }

        records.Sort((a, b) => string.CompareOrdinal(a.Card.Name, b.Card.Name));
        return records;
    }

    /// <summary>
    /// Builds an arbitrary 40-card deck from the catalog for hotseat play, respecting
    /// the standard deck rules (40 cards, at most 4 copies of any single card).
    /// </summary>
    public static List<Card> BuildStarterDeck(int seed)
    {
        var source = Load();
        var rng = new Random(seed);
        var pool = source.Select(r => r.Card).ToList();
        var deck = new List<Card>(40);
        var counts = new Dictionary<string, int>();

        var attempts = 0;
        while (deck.Count < 40 && pool.Count > 0 && attempts++ < 2000)
        {
            var card = pool[rng.Next(pool.Count)];
            if (counts.TryGetValue(card.Id, out var n) && n >= 4)
            {
                pool.Remove(card);
                continue;
            }
            counts[card.Id] = counts.TryGetValue(card.Id, out var c) ? c + 1 : 1;
            deck.Add(card);
        }

        // Pad with a fallback card if the catalog could not fill a full deck.
        while (deck.Count < 40 && pool.Count > 0)
            deck.Add(pool[deck.Count % pool.Count]);

        return deck;
    }

    private static Civilization ParseCivilization(string s) => s switch
    {
        "Light" => Civilization.Light,
        "Water" => Civilization.Water,
        "Darkness" => Civilization.Darkness,
        "Fire" => Civilization.Fire,
        "Nature" => Civilization.Nature,
        "Zero" => Civilization.Zero,
        _ => Civilization.Zero,
    };

    private static CardType ParseCardType(string s) => s switch
    {
        "Spell" => CardType.Spell,
        "EvolutionCreature" => CardType.EvolutionCreature,
        _ => CardType.Creature,
    };

    private static IEnumerable<Keyword> ParseKeywords(IEnumerable<string> raw)
    {
        foreach (var k in raw)
        {
            if (Enum.TryParse<Keyword>(k, true, out var parsed) && parsed != Keyword.None)
                yield return parsed;
        }
    }
}
