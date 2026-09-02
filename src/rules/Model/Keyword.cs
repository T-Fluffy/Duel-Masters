using System;

namespace DuelMasters.Domain;

/// <summary>
/// Recognized Duel Masters card keywords. A card may carry several.
/// Keywords drive rules like blocker ability, shield triggers, and breakers.
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

    /// <summary>All "breaker" flags; these decide how many shields a hit breaks.</summary>
    AnyBreaker = DoubleBreaker | TripleBreaker
}
