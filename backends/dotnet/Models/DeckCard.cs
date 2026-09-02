using System;

namespace DuelMasters.Server.Models;

/// <summary>A single card inclusion in a deck (with copy count).</summary>
public class DeckCard
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeckId { get; set; }
    public Deck? Deck { get; set; }
    public string CardId { get; set; } = "";
    public Card? Card { get; set; }
    public int Count { get; set; } = 1;
}
