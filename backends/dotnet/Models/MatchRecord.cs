using System;

namespace DuelMasters.Server.Models;

/// <summary>Persistent registry of networked matches. Unlike the in-memory
/// MatchRoom (which dies with the process), a MatchRecord survives restarts:
/// it is opened at host/join time and closed at winner announcement. Matches
/// that never finish stay open and unranked - results are never invented -
/// but remain visible for audit, history, and future resume support.</summary>
public class MatchRecord
{
    /// <summary>The 6-letter room code players join with. Unique per match.</summary>
    public string Code { get; set; } = "";

    public Guid? HostUserId { get; set; }
    public string HostName { get; set; } = "";
    public Guid? HostDeckId { get; set; }

    public Guid? JoinerUserId { get; set; }
    public string JoinerName { get; set; } = "";
    public Guid? JoinerDeckId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>"Player1"/"Player2" once a winner is announced, else null.</summary>
    public string? WinnerSide { get; set; }

    /// <summary>True when both seats were held by authenticated humans.</summary>
    public bool IsRanked { get; set; }
}
