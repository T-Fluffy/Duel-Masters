using System.Collections.Generic;
using System.Linq;
using DuelMasters.Domain;

namespace DuelMasters.Domain.Tests;

/// <summary>
/// Small factories used throughout the Phase 2 test suite to build cards and
/// players without repeating boilerplate.
/// </summary>
internal static class CardFactory
{
    private static int _serial;

    public static Card Creature(
        int cost,
        int power,
        Civilization civ = Civilization.Fire,
        string name = "",
        params Keyword[] keywords)
    {
        var n = string.IsNullOrWhiteSpace(name)
            ? $"Creature-{++_serial}"
            : name;
        return new Card($"c{++_serial}", n, civ, CardType.Creature, cost, power, "R", keywords);
    }

    public static Card Spell(int cost, Civilization civ = Civilization.Water, string name = "")
    {
        var n = string.IsNullOrWhiteSpace(name) ? $"Spell-{++_serial}" : name;
        return new Card($"s{++_serial}", n, civ, CardType.Spell, cost, 0, "", System.Array.Empty<Keyword>());
    }

    /// <summary>Build a player with <paramref name="size"/> cards in the deck.</summary>
    public static Player PlayerWithDeck(string name, int size, params Card[] fixedTop)
    {
        var cards = new List<Card>();
        // Prepend any caller-specified cards at the top of the deck (the first of
        // `fixedTop` is drawn first), then fill the remainder with filler creatures.
        cards.AddRange(fixedTop);
        while (cards.Count < size)
            cards.Add(Creature(cost: 1, power: 1000, name: $"{name}-filler-{cards.Count}"));

        return new Player(name, cards);
    }
}
