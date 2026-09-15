using System;

namespace DuelMasters.Server.Models;

/// <summary>An immutable record of one networked match, written server-side exactly
/// once per match. Two rows are produced together (one per side) when the winner
/// is announced: the winner row uses Won=true foreach record; the loser false.
/// The godot client never writes these rows.</summary>
public class DuelResult
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>The networked match code this record belongs to (grouping key).</summary>
    public string MatchCode { get; set; } = "";

    public bool Won { get; set; }

    /// <summary>The deck that side piloted, if any.</summary>
    public Guid? DeckId { get; set; }
    public Deck? Deck { get; set; }

    public DateTime PlayedAtUtc { get; set; } = DateTime.UtcNow;
}