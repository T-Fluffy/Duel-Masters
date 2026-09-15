using System;

namespace DuelMasters.Server.Models;

/// <summary>Per-account identity stored server-side and bound to the JWT user
/// (1:1). The Profile scene reads it through <c>ProfileController</c>. Mutations
/// (country, avatar as base64, favourite deck) are owner-authorized writes; the
/// online win/loss counters are only ever advanced by the server exactly once per
/// networked match at winner resolution (see DuelHub + MatchRoom).</summary>
public class PlayerProfile
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Free-form country string shown on the identity card.</summary>
    public string Country { get; set; } = "";

    /// <summary>Avatar image bytes encoded as base64, stored in the DB cell.</summary>
    public string AvatarBase64 { get; set; } = "";

    /// <summary>Optional favourite/focused deck for match history attribution.</summary>
    public Guid? FavouriteDeckId { get; set; }

    /// <summary>Player-chosen display name, unique across all players, shown in every scene.</summary>
    public string Nickname { get; set; } = "";

    /// <summary>Player-provided date of birth. Self-only: never exposed on public profiles.</summary>
    public DateTime? DateOfBirth { get; set; }

    /// <summary>Free-form self-description shown on the public profile. Max 500 chars.</summary>
    public string Bio { get; set; } = "";

    public int OnlineWins { get; set; }
    public int OnlineLosses { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
