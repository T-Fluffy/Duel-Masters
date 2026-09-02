using System.Collections.Generic;

namespace DuelMasters.Server.Models;

/// <summary>A card in the catalog, seeded from src/resources/data/cards.json.</summary>
public class Card
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Civilization { get; set; }
    public string CardType { get; set; } = "Creature";
    public int ManaCost { get; set; } = 1;
    public int ManaNumber { get; set; } = 1;
    public int? Power { get; set; }
    public string? Race { get; set; }
    public string ImagePath { get; set; } = "";
    public List<string> Keywords { get; set; } = new();
    public string ScriptEffectId { get; set; } = "VANILLA";

    public List<DeckCard> DeckCards { get; set; } = new();
}
