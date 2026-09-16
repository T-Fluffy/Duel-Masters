using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DuelMasters.Server.Data;
using DuelMasters.Server.Models;

namespace DuelMasters.Server.Controllers;

public record ProfileStatsResponse(int Wins, int Losses, int Total, List<RecentDuelResponse> RecentDuelResponse);
public record RecentDuelResponse(bool Won, string MatchCode, DateTime PlayedAtUtc);
public record ProfileUpdateRequest(string? Nickname, string? Country, string? AvatarBase64, Guid? FavouriteDeckId,
    DateTime? DateOfBirth, string? Bio, string? BackgroundBase64);
public record ProfileResponse(Guid UserId, string Email, string Nickname, string Country, string AvatarBase64,
    Guid? FavouriteDeckId, DateTime? DateOfBirth, string Bio, string BackgroundBase64,
    int OnlineWins, int OnlineLosses, DateTime UpdatedAtUtc);
public record PublicProfileResponse(Guid UserId, string Nickname, string Country, string AvatarBase64,
    string Bio, string BackgroundBase64, int OnlineWins, int OnlineLosses);

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ProfileController : ControllerBase
{
    private readonly AppDbContext _db;

    public ProfileController(AppDbContext db)
    {
        _db = db;
    }

    private Guid CurrentUserId =>
        Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                   ?? throw new UnauthorizedAccessException("Missing user claim."));

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var userId = CurrentUserId;
        var profile = await EnsureProfileAsync(userId);
        return Ok(ToResponse(profile));
    }

    /// <summary>Public profile lookup by exact nickname. The literal segment
    /// wins routing over "{id:guid}", and exact (case-sensitive) match mirrors
    /// the unique index, so the result is unambiguous.</summary>
    [HttpGet("by-nickname/{nickname}")]
    public async Task<IActionResult> GetByNickname(string nickname)
    {
        var profile = await _db.PlayerProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.Nickname == nickname);
        return profile is null
            ? NotFound(new { error = "Profile not found." })
            : Ok(ToPublicResponse(profile));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetPublic(Guid id)
    {
        var profile = await _db.PlayerProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.UserId == id);
        return profile is null
            ? NotFound(new { error = "Profile not found." })
            : Ok(ToPublicResponse(profile));
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var userId = CurrentUserId;
        await EnsureProfileAsync(userId);

        var wins = await _db.DuelResults.CountAsync(r => r.UserId == userId && r.Won);
        var losses = await _db.DuelResults.CountAsync(r => r.UserId == userId && !r.Won);
        var recent = await _db.DuelResults
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.PlayedAtUtc)
            .Take(10)
            .Select(r => new RecentDuelResponse(r.Won, r.MatchCode, r.PlayedAtUtc))
            .ToListAsync();
        return Ok(new ProfileStatsResponse(wins, losses, wins + losses, recent));
    }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] ProfileUpdateRequest req)
    {
        var userId = CurrentUserId;
        var profile = await EnsureProfileAsync(userId);
        var nickname = (req.Nickname ?? "").Trim();
        if (nickname.Length == 0)
            return BadRequest(new { error = "Nickname is required." });
        if (nickname.Length > 64)
            return BadRequest(new { error = "Nickname must be at most 64 characters." });
        var clash = await _db.PlayerProfiles
            .AnyAsync(p => p.UserId != userId && p.Nickname == nickname);
        if (clash)
            return Conflict(new { error = "That nickname is already taken." });
        var bio = (req.Bio ?? "").Trim();
        if (bio.Length > 500)
            bio = bio[..500];
        profile.Nickname = nickname;
        profile.Country = (req.Country ?? "").Trim();
        profile.AvatarBase64 = req.AvatarBase64 ?? "";
        profile.BackgroundBase64 = req.BackgroundBase64 ?? "";
        profile.FavouriteDeckId = req.FavouriteDeckId;
        DateTime? dob = req.DateOfBirth;
        if (dob.HasValue && dob.Value.Kind != DateTimeKind.Utc)
            dob = dob.Value.Kind == DateTimeKind.Local
                ? dob.Value.ToUniversalTime()
                : DateTime.SpecifyKind(dob.Value, DateTimeKind.Utc);
        profile.DateOfBirth = dob;
        profile.Bio = bio;
        profile.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToResponse(profile));
    }

    private async Task<PlayerProfile> EnsureProfileAsync(Guid userId)
    {
        var profile = await _db.PlayerProfiles
            .Include(p => p.User)
            .SingleOrDefaultAsync(p => p.UserId == userId);
        if (profile is not null) return profile;
        profile = new PlayerProfile { UserId = userId, Nickname = "Player-" + userId.ToString("N") };
        _db.PlayerProfiles.Add(profile);
        await _db.SaveChangesAsync();
        await _db.Entry(profile).Reference(p => p.User).LoadAsync();
        return profile;
    }

    private static ProfileResponse ToResponse(PlayerProfile p) =>
        new(p.UserId, p.User?.Email ?? "", p.Nickname, p.Country, p.AvatarBase64,
            p.FavouriteDeckId, p.DateOfBirth, p.Bio, p.BackgroundBase64,
            p.OnlineWins, p.OnlineLosses, p.UpdatedAtUtc);

    private static PublicProfileResponse ToPublicResponse(PlayerProfile p) =>
        new(p.UserId, p.Nickname, p.Country, p.AvatarBase64,
            p.Bio, p.BackgroundBase64, p.OnlineWins, p.OnlineLosses);
}
