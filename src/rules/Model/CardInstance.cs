using System;
using System.Collections.Generic;

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

    /// <summary>True once this creature has attacked this turn (drives "attacks each turn").</summary>
    public bool AttackedThisTurn { get; internal set; }

    /// <summary>
    /// Temporary power modifier applied by card effects, cleared at the start of its
    /// owner's turn. Not part of the printed power (used in combat resolution).
    /// </summary>
    public int TempPower { get; internal set; }

    /// <summary>
    /// For a creature that has creatures evolved on top of it, this stack holds the
    /// cards sitting underneath (the evolution base and anything that base itself had
    /// under it). Empty for cards not under an evolution. When the top creature leaves
    /// the battle zone, everything in this stack is sent to its owner's graveyard.
    /// </summary>
    public List<CardInstance> Underneath { get; } = new();

    public void Tap() => IsTapped = true;
    public void Untap() => IsTapped = false;

    public override string ToString() => Card.Name;
}
