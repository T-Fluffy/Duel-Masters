using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DuelMasters.Server.Data;
using DuelMasters.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DuelMasters.Server.Services;

public record CardSeedDto(
    string Id,
    string? Name,
    string? Civilization,
    string CardType,
    int ManaCost,
    int ManaNumber,
    int? Power,
    string? Race,
    string ImagePath,
    List<string>? Keywords,
    string? ScriptEffectId);

/// <summary>Loads the Phase 1 cards.json catalog into the DB on startup.</summary>
public static class CardSeeder
{
    public static void Seed(AppDbContext db, ILogger logger)
    {
        var jsonPath = Path.Combine(AppContext.BaseDirectory, "cards.json");
        if (!File.Exists(jsonPath))
        {
            logger.LogWarning("cards.json not found at {Path}; skipping seed.", jsonPath);
            return;
        }

        var existing = db.Cards.Select(c => c.Id).ToHashSet();
        if (existing.Count > 0)
        {
            logger.LogInformation("Card catalog already seeded ({Count} cards).", existing.Count);
            return;
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var dtos = JsonSerializer.Deserialize<List<CardSeedDto>>(
            File.ReadAllText(jsonPath), options) ?? new List<CardSeedDto>();

        var cards = dtos.Select(d => new Card
        {
            Id = d.Id,
            Name = d.Name,
            Civilization = d.Civilization,
            CardType = d.CardType,
            ManaCost = d.ManaCost,
            ManaNumber = d.ManaNumber,
            Power = d.Power,
            Race = d.Race,
            ImagePath = d.ImagePath,
            Keywords = d.Keywords ?? new List<string>(),
            ScriptEffectId = d.ScriptEffectId ?? "VANILLA",
        }).ToList();

        db.Cards.AddRange(cards);
        db.SaveChanges();
        logger.LogInformation("Seeded {Count} cards into the catalog.", cards.Count);
    }
}
