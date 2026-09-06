using System;

namespace DuelMasters.Domain;

/// <summary>
/// Recognized Duel Masters card keywords. A card may carry several.
/// Keywords drive rules like blocker ability, shield triggers, breakers,
/// attack restrictions and repeat effects.
/// </summary>
[Flags]
public enum Keyword
{
    None = 0,
    Blocker = 1 << 0,
    ShieldTrigger = 1 << 1,
    DoubleBreaker = 1 << 2,
    TripleBreaker = 1 << 3,
    SpeedAttacker = 1 << 4,
    Slayer = 1 << 5,
    PowerAttacker = 1 << 6,

    /// <summary>This creature cannot be blocked.</summary>
    Unblockable = 1 << 7,

    /// <summary>This creature cannot attack the opponent directly.</summary>
    CannotAttackPlayers = 1 << 8,

    /// <summary>This creature cannot attack creatures.</summary>
    CannotAttackCreatures = 1 << 9,

    /// <summary>This creature may attack tapped or untapped creatures.</summary>
    CanAttackUntappedCreatures = 1 << 10,

    /// <summary>Other creatures cannot attack this creature.</summary>
    CannotBeAttacked = 1 << 11,

    /// <summary>When this creature can attack, it must do so ("attacks each turn if able").</summary>
    AttacksEachTurn = 1 << 12,

    /// <summary>After resolving, this spell is put into your mana zone instead of the graveyard.</summary>
    Charger = 1 << 13,

    /// <summary>At the end of each turn, if this is the only creature in its owner's battle zone, destroy it.</summary>
    Survivor = 1 << 14,

    /// <summary>Stealth: this creature cannot be blocked.</summary>
    Stealth = 1 << 15,

    /// <summary>This creature can be summoned only if its owner cast a spell this turn.</summary>
    SummonRequiresSpellCast = 1 << 16,

    /// <summary>This creature can't attack while the opponent has more creatures in the battle zone.</summary>
    CannotAttackOutnumbered = 1 << 17,

    /// <summary>All "breaker" flags; these decide how many shields a hit breaks.</summary>
    AnyBreaker = DoubleBreaker | TripleBreaker
}