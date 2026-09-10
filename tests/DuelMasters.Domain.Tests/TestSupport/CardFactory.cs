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
        var n = string.IsNullOrWhiteSpace(name) ? $"Creature-{++_serial}" : name;
        return new Card($"c{++_serial}", n, civ, CardType.Creature, cost, power, "R", keywords);
    }

    public static Card Creature(
        int cost,
        int power,
        Civilization civ,
        string name,
        CardEffect? effect,
        params Keyword[] keywords)
    {
        var n = string.IsNullOrWhiteSpace(name) ? $"Creature-{++_serial}" : name;
        return new Card($"c{++_serial}", n, civ, CardType.Creature, cost, power, "R", keywords,
            effect is null ? System.Array.Empty<CardEffect>() : new[] { effect });
    }

    /// <summary>Creature with a custom race, optional effects and keywords.</summary>
    public static Card CreatureWithRace(
        int cost,
        int power,
        Civilization civ,
        string name,
        string race,
        IEnumerable<CardEffect>? effects = null,
        params Keyword[] keywords)
    {
        var n = string.IsNullOrWhiteSpace(name) ? $"Creature-{++_serial}" : name;
        return new Card($"c{++_serial}", n, civ, CardType.Creature, cost, power, race, keywords,
            effects ?? System.Array.Empty<CardEffect>());
    }

    /// <summary>Evolution creature that must be placed on a creature of <paramref name="evolvesFrom"/> race.</summary>
    public static Card Evolution(
        int cost,
        int power,
        Civilization civ,
        string name,
        string race,
        string evolvesFrom,
        IEnumerable<CardEffect>? effects = null,
        params Keyword[] keywords)
    {
        var n = string.IsNullOrWhiteSpace(name) ? $"Evo-{++_serial}" : name;
        return new Card($"e{++_serial}", n, civ, CardType.EvolutionCreature, cost, power, race, keywords,
            effects ?? System.Array.Empty<CardEffect>(), evolvesFrom);
    }

    public static Card Spell(int cost, Civilization civ = Civilization.Water, string name = "")
    {
        var n = string.IsNullOrWhiteSpace(name) ? $"Spell-{++_serial}" : name;
        return new Card($"s{++_serial}", n, civ, CardType.Spell, cost, 0, "", System.Array.Empty<Keyword>());
    }

    public static Card Spell(int cost, Civilization civ, string name, CardEffect effect)
    {
        var n = string.IsNullOrWhiteSpace(name) ? $"Spell-{++_serial}" : name;
        return new Card($"s{++_serial}", n, civ, CardType.Spell, cost, 0, "", System.Array.Empty<Keyword>(), new[] { effect });
    }

    public static Card Spell(int cost, Civilization civ, string name, CardEffect effect, params Keyword[] keywords)
    {
        var n = string.IsNullOrWhiteSpace(name) ? $"Spell-{++_serial}" : name;
        return new Card($"s{++_serial}", n, civ, CardType.Spell, cost, 0, "", keywords, new[] { effect });
    }

    /// <summary>Creature with one activated Tap Ability, no other effects.</summary>
    public static Card TapCreature(
        int cost,
        int power,
        Civilization civ = Civilization.Fire,
        string name = "",
        CardEffect? tapAbility = null,
        params Keyword[] keywords)
    {
        var n = string.IsNullOrWhiteSpace(name) ? $"TapCreature-{++_serial}" : name;
        var ability = tapAbility is null ? new[] { new CardEffect(EffectId.Tap_NotModelled, EffectTargetScope.None) }
                                         : new[] { tapAbility };
        return new Card($"c{++_serial}", n, civ, CardType.Creature, cost, power, "R", keywords,
            System.Array.Empty<CardEffect>(), "", ability);
    }

    /// <summary>Creature with a custom race and one activated Tap Ability.</summary>
    public static Card TapCreatureWithRace(
        int cost,
        int power,
        Civilization civ,
        string name,
        string race,
        CardEffect? tapAbility = null)
    {
        var n = string.IsNullOrWhiteSpace(name) ? $"TapRace-{++_serial}" : name;
        var ability = tapAbility is null ? new[] { new CardEffect(EffectId.Tap_NotModelled, EffectTargetScope.None) }
                                         : new[] { tapAbility };
        return new Card($"c{++_serial}", n, civ, CardType.Creature, cost, power, race, System.Array.Empty<Keyword>(),
            System.Array.Empty<CardEffect>(), "", ability);
    }

    /// <summary>DM-06 Crew card: a tap ability that any own creature of <paramref name="crewCiv"/> may pay.</summary>
    public static Card CrewCard(
        int cost,
        int power,
        Civilization civ,
        string name,
        string crewCiv,
        CardEffect? tapAbility = null)
    {
        var n = string.IsNullOrWhiteSpace(name) ? $"Crew-{++_serial}" : name;
        var ability = tapAbility is null ? new[] { new CardEffect(EffectId.Tap_NotModelled, EffectTargetScope.None) }
                                         : new[] { tapAbility };
        return new Card($"c{++_serial}", n, civ, CardType.Creature, cost, power, "R", System.Array.Empty<Keyword>(),
            System.Array.Empty<CardEffect>(), "", ability, crewCiv);
    }

    /// <summary>Shorthand for authoring <see cref="CardEffect"/> in tests.</summary>
    public static CardEffect Eff(EffectId id, EffectTargetScope target = EffectTargetScope.None, int value = 0, string data = "")
        => new(id, target, value, data);

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
