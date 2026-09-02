using System;
using System.Collections.Generic;

namespace DuelMasters.Server.Models;

/// <summary>A user's deck. Deck rules: 40 cards, max 4 copies of any card.</summary>
public class Deck
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<DeckCard> Cards { get; set; } = new();
}
