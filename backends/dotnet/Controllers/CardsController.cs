using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DuelMasters.Server.Data;
using DuelMasters.Server.Models;

namespace DuelMasters.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CardsController : ControllerBase
{
    private readonly AppDbContext _db;

    public CardsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? set = null,
        [FromQuery] string? civilization = null,
        [FromQuery] string? cardType = null,
        [FromQuery] int? powerfulOrEqual = null)
    {
        IQueryable<Card> q = _db.Cards;
        if (!string.IsNullOrWhiteSpace(set))
            q = q.Where(c => c.Id.StartsWith(set.ToLowerInvariant()));
        if (!string.IsNullOrWhiteSpace(civilization))
            q = q.Where(c => c.Civilization == civilization);
        if (!string.IsNullOrWhiteSpace(cardType))
            q = q.Where(c => c.CardType == cardType);
        if (powerfulOrEqual.HasValue)
            q = q.Where(c => c.Power != null && c.Power >= powerfulOrEqual);

        var cards = await q
            .OrderBy(c => c.Id)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Civilization,
                c.CardType,
                c.ManaCost,
                c.ManaNumber,
                c.Power,
                c.Race,
                c.ImagePath,
                c.Keywords,
                c.ScriptEffectId,
            })
            .ToListAsync();

        return Ok(new { count = cards.Count, cards });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        var card = await _db.Cards.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id);
        return card is null ? NotFound(new { error = "Card not found." }) : Ok(card);
    }
}
