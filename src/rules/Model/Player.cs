using System;
using System.Collections.Generic;
using System.Linq;

namespace DuelMasters.Domain;

/// <summary>
/// A player's game-relevant state: deck order plus the in-play zones that the
/// rules engine reads and mutates each turn.
/// </summary>
public sealed class Player
{
    public Player(string name, IEnumerable<Card> deck)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        // Top of the deck is index 0; the engine draws from the top.
        Deck = deck.ToList();
    }

    public string Name { get; }

    /// <summary>Cards still in the deck, top first (index 0 is the top).</summary>
    public List<Card> Deck { get; }

    public List<CardInstance> Hand { get; } = new();
    public List<CardInstance> ManaZone { get; } = new();
    public List<CardInstance> BattleZone { get; } = new();
    public List<CardInstance> Graveyard { get; } = new();

    /// <summary>Face-down shield cards. The outer list order is "oldest first".</summary>
    public List<Card> Shields { get; } = new();

    public int ShieldCount => Shields.Count;

    public IEnumerable<CardInstance> TappedMana => ManaZone.Where(m => m.IsTapped);
    public IEnumerable<CardInstance> UntappedMana => ManaZone.Where(m => !m.IsTapped);

    /// <summary>Total number of mana cards available (tapped mana = spent).</summary>
    public int TotalMana => ManaZone.Count;

    public bool HasLost { get; internal set; }
}
