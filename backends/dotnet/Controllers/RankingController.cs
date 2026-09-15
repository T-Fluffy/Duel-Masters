using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DuelMasters.Server.Data;

namespace DuelMasters.Server.Controllers;

public record LeaderboardEntry(Guid UserId, string Nickname, string Country, string AvatarBase64,
    int Rating, int OnlineWins, int OnlineLosses);
public record SelfRankResponse(Guid UserId, string Nickname, int Rating, int Rank,
    int OnlineWins, int OnlineLosses);

/// <summary>Read-only ranked ladder over ELO ratings. Ratings move only on
/// human-vs-human games at winner resolution (see DuelHub); this controller
/// never writes. Entries expose public profile fields only - never email or
/// date of birth.</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class RankingController : ControllerBase
{
    private readonly AppDbContext _db;

    public RankingController(AppDbContext db)
    {
        _db = db;
    }

    private Guid CurrentUserId =>
        Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                   ?? throw new UnauthorizedAccessException("Missing user claim."));

    [HttpGet("leaderboard")]
    public async Task<IActionResult> GetLeaderboard([FromQuery] int top = 50)
    {
        top = Math.Clamp(top, 1, 100);
        var rows = await _db.PlayerProfiles
            .AsNoTracking()
            .OrderByDescending(p => p.Rating)
            .ThenBy(p => p.Nickname)
            .Take(top)
            .Select(p => new LeaderboardEntry(p.UserId, p.Nickname, p.Country, p.AvatarBase64,
                p.Rating, p.OnlineWins, p.OnlineLosses))
            .ToListAsync();
        return Ok(rows);
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetMine()
    {
        var userId = CurrentUserId;
        var me = await _db.PlayerProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.UserId == userId);
        if (me is null)
            return NotFound(new { error = "Profile not found." });
        var rank = await _db.PlayerProfiles.CountAsync(p => p.Rating > me.Rating) + 1;
        return Ok(new SelfRankResponse(me.UserId, me.Nickname, me.Rating, rank,
            me.OnlineWins, me.OnlineLosses));
    }
}
