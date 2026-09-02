using System;
using System.Collections.Generic;

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
        IEnumerable<Keyword> keywords = null!)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Civilization = civilization;
        CardType = cardType;
        ManaCost = manaCost >= 0 ? manaCost : throw new ArgumentOutOfRangeException(nameof(manaCost));
        Power = power;
        Race = race ?? "";
        Keywords = keywords as IReadOnlySet<Keyword> ?? new HashSet<Keyword>(keywords ?? Array.Empty<Keyword>());
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

    public bool HasKeyword(Keyword k) => Keywords.Contains(k);

    public bool IsCreature => CardType == CardType.Creature || CardType == CardType.EvolutionCreature;

    /// <summary>How many shields one hit from this card breaks (1 normally).</summary>
    public int BreakerCount =>
        HasKeyword(Keyword.TripleBreaker) ? 3 :
        HasKeyword(Keyword.DoubleBreaker) ? 2 : 1;

    public override string ToString() => Name;
}
