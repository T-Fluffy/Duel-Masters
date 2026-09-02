using DuelMasters.Domain;
using Godot;

namespace DuelMasters.Resources;

/// <summary>
/// Central mapping of Duel Masters civilizations to their signature colors,
/// used across the client for card frames and UI accents.
/// </summary>
public static class CivilizationPalette
{
    public static Color Color(Civilization civ) => civ switch
    {
        Civilization.Light => new Color("ffe08a"),
        Civilization.Water => new Color("3fa9f5"),
        Civilization.Darkness => new Color("7a5fb8"),
        Civilization.Fire => new Color("e4572e"),
        Civilization.Nature => new Color("4fae4f"),
        Civilization.Zero => new Color("9a9a9a"),
        _ => new Color("9a9a9a"),
    };
}
