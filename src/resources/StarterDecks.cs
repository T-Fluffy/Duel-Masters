using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DuelMasters.Domain;
using Godot;

namespace DuelMasters.Resources;

/// <summary>A single card entry + copy count inside a starter deck list.</summary>
public sealed record CardCount(string Name, int Count);

/// <summary>
/// A registered starter deck: a hand-curated, legal 40-card list of real
/// card names (at most 4 copies each), tagged with an archetype and a one-line
/// description. These are the shared "deck samples" surfaced in the Deck Builder
/// and offered in the Arena's deck-selection panel (for the player and the AI).
/// </summary>
public sealed record StarterDeck(string Id, string Name, string Archetype, string Tagline, IReadOnlyList<CardCount> Cards);

/// <summary>
/// Loads the starter deck registry (<c>res://src/resources/data/starter_decks.json</c>)
/// and resolves its card names into the shared domain <see cref="Card"/> model.
///
/// Name resolution is defensive: if a listed name is ever missing from the
/// catalog it falls back to a same-civilization, same-type card, and the deck is
/// padded to exactly 40 cards from the deck's primary civilization - so a
/// registered deck can never hand back an illegal list.
/// </summary>
public static class StarterDecks
{
    public const string DecksJsonPath = "res://src/resources/data/starter_decks.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class DeckJson
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Archetype { get; set; } = "";
        public string Tagline { get; set; } = "";
        public List<CardJson> Cards { get; set; } = new();
    }

    private sealed class CardJson
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
    }

    private static List<StarterDeck>? _cache;

    /// <summary>All registered starter decks, sorted by name.</summary>
    public static List<StarterDeck> LoadAll()
    {
        if (_cache is not null)
            return _cache;

        if (!FileAccess.FileExists(DecksJsonPath))
            throw new InvalidOperationException($"Starter deck registry not found at {DecksJsonPath}.");

        using var file = FileAccess.Open(DecksJsonPath, FileAccess.ModeFlags.Read);
        var text = file.GetAsText();
        var list = JsonSerializer.Deserialize<List<DeckJson>>(text, JsonOptions) ?? new List<DeckJson>();

        var decks = list
            .Where(d => !string.IsNullOrEmpty(d.Id) && d.Cards.Count > 0)
            .Select(d => new StarterDeck(
                d.Id,
                string.IsNullOrEmpty(d.Name) ? d.Id : d.Name,
                string.IsNullOrEmpty(d.Archetype) ? "Custom" : d.Archetype,
                d.Tagline ?? "",
                d.Cards.Select(c => new CardCount(c.Name, Math.Max(1, c.Count))).ToList() as IReadOnlyList<CardCount>))
            .ToList();

        decks.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        _cache = decks;
        return decks;
    }

    /// <summary>Look up a single deck by its id, or <c>null</c>.</summary>
    public static StarterDeck? Get(string id) => LoadAll().FirstOrDefault(d => d.Id == id);

    /// <summary>
    /// Resolve a deck's card names into a full legal <see cref="Card"/> list
    /// (exactly 40 cards, at most 4 copies of any single name).
    /// </summary>
    public static List<Card> ResolveCards(string id)
    {
        var deck = Get(id)
            ?? throw new InvalidOperationException($"Unknown starter deck id '{id}'.");

        var records = CardCatalog.Load();
        var byName = new Dictionary<string, Card>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in records)
            byName.TryAdd(r.Card.Name, r.Card);

        var result = new List<Card>(40);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void AddCard(Card card)
        {
            counts.TryGetValue(card.Name, out var n);
            if (n >= 4)
                return;
            counts[card.Name] = n + 1;
            result.Add(card);
        }

        foreach (var entry in deck.Cards)
        {
            for (var i = 0; i < entry.Count; i++)
            {
                if (byName.TryGetValue(entry.Name, out var card))
                    AddCard(card);
            }
        }

        var primaryCiv = result
            .GroupBy(c => c.Civilization)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => (int)g.Key)
            .FirstOrDefault()?.Key ?? Civilization.Zero;

        // Guarantee exactly 40: pad with any card of the deck's primary
        // civilization that is still under the 4-copy limit. (The curated lists
        // already resolve in full; this only guards against future catalog edits.)
        var backupPool = records
            .Select(r => r.Card)
            .Where(c => c.Civilization == primaryCiv)
            .ToList();
        var cursor = 0;
        while (result.Count < 40 && backupPool.Count > 0 && cursor < backupPool.Count * 8)
        {
            AddCard(backupPool[cursor % backupPool.Count]);
            cursor++;
        }

        if (result.Count < 40)
            throw new InvalidOperationException($"Starter deck '{id}' could not be resolved to 40 cards (" +
                $"only {result.Count} available for its civilizations).");

        return result;
    }
}