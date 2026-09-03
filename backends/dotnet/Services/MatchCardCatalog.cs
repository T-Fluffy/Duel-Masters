using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DuelMasters.Domain;

namespace DuelMasters.Server.Services;

/// <summary>
/// Reads the Phase 1 <c>cards.json</c> catalog (shipped to the server's output
/// directory) into the shared domain <see cref="Card"/> model, and builds random
/// legal 40-card decks for networked matches. Postgres/EF is not required for
/// match play; the file is the single source of truth here, mirroring the client.
/// </summary>
public static class MatchCardCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class CardJson
    {
        public string Id { get; set; } = "";
        public string? Name { get; set; }
        public string? Civilization { get; set; }
        public string CardType { get; set; } = "Creature";
        public int ManaCost { get; set; }
        public int? Power { get; set; }
        public string Race { get; set; } = "";
        public List<string>? Keywords { get; set; }
    }

    private static List<Card>? _cache;

    /// <summary>All named cards in the shipped catalog (cached after first load).</summary>
    public static List<Card> Load()
    {
        if (_cache is not null)
            return _cache;

        var jsonPath = Path.Combine(AppContext.BaseDirectory, "cards.json");
        if (!File.Exists(jsonPath))
            throw new InvalidOperationException($"cards.json not found at {jsonPath}.");

        var list = JsonSerializer.Deserialize<List<CardJson>>(File.ReadAllText(jsonPath), JsonOptions)
                   ?? new List<CardJson>();

        var cards = new List<Card>(list.Count);
        foreach (var c in list)
        {
            if (string.IsNullOrEmpty(c.Name) || string.IsNullOrEmpty(c.Civilization))
                continue;

            var keywords = (c.Keywords ?? new List<string>())
                .Where(k => Enum.TryParse<Keyword>(k, ignoreCase: true, out var _))
                .Select(k => Enum.Parse<Keyword>(k, ignoreCase: true))
                .Where(k => k != Keyword.None)
                .ToList();

            cards.Add(new Card(
                c.Id,
                c.Name!,
                ParseCivilization(c.Civilization!),
                ParseCardType(c.CardType),
                c.ManaCost,
                c.Power ?? 0,
                c.Race ?? "",
                keywords));
        }

        cards.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        _cache = cards;
        return cards;
    }

    /// <summary>
    /// Build an arbitrary 40-card deck from the catalog, respecting the standard
    /// deck rules (40 cards, at most 4 copies of any single card).
    /// </summary>
    public static List<Card> BuildRandomDeck(Random rng)
    {
        var pool = Load();
        if (pool.Count == 0)
            throw new InvalidOperationException("Card catalog is empty; cannot build a deck.");

        var deck = new List<Card>(40);
        var counts = new Dictionary<string, int>();
        var attempts = 0;
        while (deck.Count < 40 && pool.Count > 0 && attempts++ < 5000)
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

        return deck;
    }

    private static Civilization ParseCivilization(string s) => s.ToLowerInvariant() switch
    {
        "light" => Civilization.Light,
        "water" => Civilization.Water,
        "darkness" => Civilization.Darkness,
        "fire" => Civilization.Fire,
        "nature" => Civilization.Nature,
        "zero" => Civilization.Zero,
        _ => Civilization.Zero,
    };

    private static CardType ParseCardType(string s) => s.ToLowerInvariant() switch
    {
        "spell" => CardType.Spell,
        "evolutioncreature" => CardType.EvolutionCreature,
        _ => CardType.Creature,
    };
}
