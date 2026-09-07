using System;
using System.Collections.Generic;
using System.Linq;

namespace DuelMasters.Domain;

/// <summary>
/// A single playable card in the domain model.
/// Carries the fields produced by the Phase 1 ingestion pipeline plus a
/// lightweight keyword set that drives the rules engine.
/// </summary>
public sealed class Card
{
    public Card(
        string id,
        string name,
        Civilization civilization,
        CardType cardType,
        int manaCost,
        int power = 0,
        string race = "",
        IEnumerable<Keyword> keywords = null!,
        IEnumerable<CardEffect> effects = null!,
        string evolutionOf = "")
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Civilization = civilization;
        CardType = cardType;
        ManaCost = manaCost >= 0 ? manaCost : throw new ArgumentOutOfRangeException(nameof(manaCost));
        Power = power;
        Race = race ?? "";
        Keywords = keywords as IReadOnlySet<Keyword> ?? new HashSet<Keyword>(keywords ?? Array.Empty<Keyword>());
        Effects = effects as IReadOnlyList<CardEffect> ?? (effects ?? Array.Empty<CardEffect>()).ToArray();
        EvolutionOf = evolutionOf ?? "";
    }

    public string Id { get; }
    public string Name { get; }
    public Civilization Civilization { get; }
    public CardType CardType { get; }
    public int ManaCost { get; }
    public int Power { get; }
    public string Race { get; }

    /// <summary>Keyword flags (blocker, shield trigger, breakers, ...).</summary>
    public IReadOnlySet<Keyword> Keywords { get; }

    /// <summary>Named game-rule effects this card resolves (empty for vanilla cards).</summary>
    public IReadOnlyList<CardEffect> Effects { get; }

    public bool HasKeyword(Keyword k) => Keywords.Contains(k);

    public CardEffect? EffectOf(EffectId id) => Effects.FirstOrDefault(e => e.Id == id);

    public bool IsCreature => CardType == CardType.Creature || CardType == CardType.EvolutionCreature;

    /// <summary>
    /// The race an Evolution creature must be placed on top of (empty for everything
    /// else). Evolution creatures cannot be summoned normally - per the official
    /// rules they enter the battle zone only by evolving onto one of your creatures
    /// whose race matches this value - but they can still be charged to the mana
    /// zone like any other hand card.
    /// </summary>
    public string EvolutionOf { get; }

    /// <summary>True for an Evolution creature that carries real evolution rules.</summary>
    public bool IsEvolution => CardType == CardType.EvolutionCreature && !string.IsNullOrWhiteSpace(EvolutionOf);

    /// <summary>How many shields one hit from this card breaks (1 normally).</summary>
    public int BreakerCount =>
        HasKeyword(Keyword.TripleBreaker) ? 3 :
        HasKeyword(Keyword.DoubleBreaker) ? 2 : 1;

    public override string ToString() => Name;
}
