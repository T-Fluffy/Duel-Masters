namespace DuelMasters.Domain;

/// <summary>
/// The sequential phases of a player's turn in the order the engine enforces them:
/// Untap, Draw, Main (mana + summon + attacks), End.
/// </summary>
public enum GamePhase
{
    Untap,
    Draw,
    Main,
    End
}
