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

public record DeckLine(string CardId, int Count);
public record DeckRequest(string Name, List<DeckLine> Cards);
public record DeckCardResponse(string CardId, int Count);
public record DeckResponse(Guid Id, string Name, int CardCount, List<DeckCardResponse> Cards);

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DecksController : ControllerBase
{
    private const int MinCards = 40;
    private const int MaxCards = 40;
    private const int MaxCopies = 4;

    private readonly AppDbContext _db;

    public DecksController(AppDbContext db)
    {
        _db = db;
    }

    private Guid CurrentUserId =>
        Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                   ?? throw new UnauthorizedAccessException("Missing user claim."));

    [HttpGet]
    public async Task<IActionResult> GetMyDecks()
    {
        var userId = CurrentUserId;
        var decks = await _db.Decks
            .Where(d => d.UserId == userId)
            .Include(d => d.Cards)
            .OrderByDescending(d => d.UpdatedAt)
            .Select(d => new DeckResponse(
                d.Id, d.Name, d.Cards.Sum(c => c.Count),
                d.Cards.Select(c => new DeckCardResponse(c.CardId, c.Count)).ToList()))
            .ToListAsync();
        return Ok(decks);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetDeck(Guid id)
    {
        var userId = CurrentUserId;
        var deck = await _db.Decks
            .Include(d => d.Cards)
            .SingleOrDefaultAsync(d => d.Id == id && d.UserId == userId);
        return deck is null
            ? NotFound(new { error = "Deck not found." })
            : Ok(new DeckResponse(deck.Id, deck.Name, deck.Cards.Sum(c => c.Count),
                deck.Cards.Select(c => new DeckCardResponse(c.CardId, c.Count)).ToList()));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] DeckRequest req)
    {
        var userId = CurrentUserId;
        var lines = req.Cards ?? new List<DeckLine>();

        var (valid, error, linesOk) = await ValidateAndNormalize(lines);
        if (!valid)
            return BadRequest(new { error });

        var deck = new Deck { UserId = userId, Name = req.Name.Trim() };
        deck.Cards = linesOk!.Select(l => new DeckCard
        {
            Deck = deck,
            CardId = l.CardId,
            Count = l.Count,
        }).ToList();

        _db.Decks.Add(deck);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetDeck), new { id = deck.Id },
            new DeckResponse(deck.Id, deck.Name, deck.Cards.Sum(c => c.Count),
                deck.Cards.Select(c => new DeckCardResponse(c.CardId, c.Count)).ToList()));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] DeckRequest req)
    {
        var userId = CurrentUserId;
        var deck = await _db.Decks
            .Include(d => d.Cards)
            .SingleOrDefaultAsync(d => d.Id == id && d.UserId == userId);
        if (deck is null)
            return NotFound(new { error = "Deck not found." });

        var lines = req.Cards ?? new List<DeckLine>();
        var (valid, error, linesOk) = await ValidateAndNormalize(lines);
        if (!valid)
            return BadRequest(new { error });

        deck.Name = req.Name.Trim();
        deck.UpdatedAt = DateTime.UtcNow;
        _db.DeckCards.RemoveRange(deck.Cards);
        deck.Cards = linesOk!.Select(l => new DeckCard
        {
            Deck = deck,
            CardId = l.CardId,
            Count = l.Count,
        }).ToList();

        await _db.SaveChangesAsync();
        return Ok(new DeckResponse(deck.Id, deck.Name, deck.Cards.Sum(c => c.Count),
            deck.Cards.Select(c => new DeckCardResponse(c.CardId, c.Count)).ToList()));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = CurrentUserId;
        var deck = await _db.Decks.SingleOrDefaultAsync(d => d.Id == id && d.UserId == userId);
        if (deck is null)
            return NotFound(new { error = "Deck not found." });

        _db.Decks.Remove(deck);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private async Task<(bool, string?, List<DeckLine>?)> ValidateAndNormalize(List<DeckLine> lines)
    {
        // Merge duplicate lines, reject >4 copies, and check total == 40.
        var merged = lines
            .GroupBy(l => l.CardId.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Count));

        foreach (var (cardId, count) in merged)
            if (count > MaxCopies)
                return (false, $"Card '{cardId}' exceeds the {MaxCopies}-copy limit.", null);

        var total = merged.Values.Sum();
        if (total < MinCards || total > MaxCards)
            return (false, $"A deck must contain exactly {MaxCards} cards (got {total}).", null);

        // Ensure all card ids exist in the catalog.
        var knownIds = await _db.Cards
            .Where(c => merged.Keys.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync();
        var missing = merged.Keys.Except(knownIds).ToList();
        if (missing.Count > 0)
            return (false, $"Unknown card id(s): {string.Join(", ", missing)}.", null);

        var normalized = merged
            .Select(kv => new DeckLine(kv.Key, kv.Value))
            .ToList();
        return (true, null, normalized);
    }
}
