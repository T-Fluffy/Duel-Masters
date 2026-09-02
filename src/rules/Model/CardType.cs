namespace DuelMasters.Domain;

/// <summary>
/// The high-level card types used by the rules engine.
/// Mirrors the Phase 1 ingestion schema (<c>cardType</c> field).
/// </summary>
public enum CardType
{
    Creature,
    Spell,
    EvolutionCreature
}
