using System;

namespace DuelMasters.Domain;

/// <summary>
/// A concrete copy of a <see cref="Card"/> in a specific zone, carrying the
/// per-copy rules state that matters at runtime (tap, summoning sickness, owner).
/// </summary>
public sealed class CardInstance
{
    public CardInstance(Card card, Player? owner = null)
    {
        Card = card ?? throw new ArgumentNullException(nameof(card));
        Owner = owner;
    }

    public Card Card { get; }

    /// <summary>The player who owns this copy, captured when the engine creates it.</summary>
    public Player? Owner { get; set; }

    public Zone Zone { get; internal set; }

    /// <summary>True while the card is tapped (used to produce mana or attack).</summary>
    public bool IsTapped { get; internal set; }

    /// <summary>True if this creature was summoned this turn and is still sick (can't attack).</summary>
    public bool IsSummoningSick { get; internal set; }

    public void Tap() => IsTapped = true;
    public void Untap() => IsTapped = false;

    public override string ToString() => Card.Name;
}
