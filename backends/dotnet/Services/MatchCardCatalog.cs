using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DuelMasters.Domain;

namespace DuelMasters.Server.Services;

/// <summary>
/// Reads the Phase 1 <c>cards.json</c> catalog (shipped to the server's output
/// directory) into the shared domain <see cref="Card"/> model, and builds decks
/// for networked matches. Postgres/EF is not required for match play; the file is
/// the single source of truth here, mirroring the client.
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
        public List<string> Keywords { get; set; } = new();
        public string EvolutionOf { get; set; } = "";
        public List<EffectJson> Effects { get; set; } = new();
        public List<EffectJson> TapAbilities { get; set; } = new();
    }

    private sealed class EffectJson
    {
        public string? Id { get; set; }
        public string? Target { get; set; }
        public int Value { get; set; }
        public string? Data { get; set; }
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

            var keywords = ParseKeywords(c.Keywords);
            var effects = ParseEffects(c.Effects);
            var tapAbilities = ParseEffects(c.TapAbilities);

            cards.Add(new Card(
                c.Id,
                c.Name!,
                ParseCivilization(c.Civilization!),
                ParseCardType(c.CardType),
                c.ManaCost,
                c.Power ?? 0,
                c.Race ?? "",
                keywords,
                effects,
                c.EvolutionOf ?? "",
                tapAbilities));
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
            deck.Add(card.Clone());
        }

        return deck;
    }

    /// <summary>
    /// Build a 40-card deck from explicit (card id, copy count) lines, mirroring the
    /// deck-builder validation (40 cards, at most 4 copies, ids must exist). Returns
    /// an error message when the lines are illegal.
    /// </summary>
    public static (bool Ok, string? Error, List<Card>? Deck) BuildDeck(IEnumerable<(string CardId, int Count)> lines)
    {
        var merged = (lines ?? Array.Empty<(string, int)>())
            .GroupBy(l => l.CardId.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Count));

        foreach (var (_, count) in merged)
            if (count > 4)
                return (false, "A deck can contain at most 4 copies of any card.", null);

        var total = merged.Values.Sum();
        if (total is < 40 or > 40)
            return (false, $"A deck must contain exactly 40 cards (got {total}).", null);

        var byId = Load().ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        var missing = merged.Keys.Where(id => !byId.ContainsKey(id)).ToList();
        if (missing.Count > 0)
            return (false, $"Unknown card id(s): {string.Join(", ", missing)}.", null);

        var deck = new List<Card>(40);
        foreach (var (id, count) in merged)
            for (var i = 0; i < count; i++)
                deck.Add(byId[id].Clone());

        return (true, null, deck);
    }

    private static Civilization ParseCivilization(string s) => s.ToLowerInvariant() switch
    {
        "light" => Civilization.Light,
        "water" => Civilization.Water,
        "darkness" => Civilization.Darkness,
        "fire" => Civilization.Fire,
        "nature" => Civilization.Nature,
        _ => Civilization.Zero,
    };

    private static CardType ParseCardType(string s) => s.ToLowerInvariant() switch
    {
        "spell" => CardType.Spell,
        "evolutioncreature" => CardType.EvolutionCreature,
        _ => CardType.Creature,
    };

    private static IEnumerable<Keyword> ParseKeywords(IEnumerable<string> raw)
    {
        foreach (var k in raw)
        {
            if (Enum.TryParse<Keyword>(k, ignoreCase: true, out var parsed) && parsed != Keyword.None)
                yield return parsed;
        }
    }

    private static IEnumerable<CardEffect> ParseEffects(IEnumerable<EffectJson> raw)
    {
        foreach (var e in raw)
        {
            if (e.Id is null || !Enum.TryParse<EffectId>(e.Id, ignoreCase: true, out var id) || id == EffectId.None)
                continue;
            var target = EffectTargetScope.None;
            if (!string.IsNullOrEmpty(e.Target))
                Enum.TryParse<EffectTargetScope>(e.Target, ignoreCase: true, out target);
            yield return new CardEffect(id, target, e.Value, e.Data ?? "");
        }
    }
}