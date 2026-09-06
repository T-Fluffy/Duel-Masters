using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DuelMasters.Domain;
using Xunit;

namespace DuelMasters.Domain.Tests;

/// <summary>
/// Guards the authoring data layered on top of the rules engine: every starter
/// deck must resolve against the catalog, deck rules must hold (40 cards, at most
/// 4 copies), and every keyword / effect written into the catalog must map to a
/// real engine enum. This mirrors the parser inside CardCatalog without needing
/// the Godot runtime.
/// </summary>
public class CatalogDataTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string CardsJsonPath = Path.Combine(RepoRoot, "src", "resources", "data", "cards.json");
    private static readonly string DecksJsonPath = Path.Combine(RepoRoot, "src", "resources", "data", "starter_decks.json");

    private static string? TryResolve(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "resources", "data", "cards.json")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static string FindRepoRoot()
    {
        var probes = new[]
        {
            AppContext.BaseDirectory,
            Environment.CurrentDirectory,
            Path.GetFullPath("src/resources/data/cards.json") is var probe && File.Exists(probe)
                ? Directory.GetParent(probe)!.Parent!.Parent!.FullName
                : null,
        };
        foreach (var start in probes)
        {
            if (start is null) continue;
            var root = TryResolve(start);
            if (root is not null) return root;
        }
        throw new InvalidOperationException("Could not locate the repo root from the test output directory.");
    }

    private static JsonElement LoadArray(string path)
    {
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, List<JsonElement>> LoadCards()
    {
        var cards = LoadArray(CardsJsonPath);
        var byName = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        foreach (var c in cards.EnumerateArray())
        {
            if (!c.TryGetProperty("name", out var nameEl) || string.IsNullOrEmpty(nameEl.GetString()))
                continue;
            var name = nameEl.GetString()!;
            if (!byName.TryGetValue(name, out var list))
                byName[name] = list = new List<JsonElement>();
            list.Add(c);
        }
        return byName;
    }

    [Fact]
    public void AllStarterDecks_AreFortyCards_WithAtMostFourCopies_And_ResolveAgainstCatalog()
    {
        var byName = LoadCards();
        var decks = LoadArray(DecksJsonPath);

        Assert.NotEmpty(decks.EnumerateArray());
        foreach (var deck in decks.EnumerateArray())
        {
            var deckId = deck.GetProperty("id").GetString()!;
            var total = 0;
            var copies = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var entry in deck.GetProperty("cards").EnumerateArray())
            {
                var cardName = entry.GetProperty("name").GetString()!;
                var count = entry.GetProperty("count").GetInt32();

                Assert.True(byName.ContainsKey(cardName),
                    $"Deck '{deckId}' references '{cardName}' which does not exist in cards.json.");

                copies.TryGetValue(cardName, out var prior);
                copies[cardName] = prior + count;
                total += count;
            }

            Assert.Equal(40, total);
            foreach (var kv in copies)
                Assert.True(kv.Value <= 4, $"Deck '{deckId}' runs {kv.Value} copies of '{kv.Key}' (limit 4).");
        }
    }

    [Fact]
    public void EveryCatalogKeyword_MapsToARealKeywordEnum()
    {
        var cards = LoadArray(CardsJsonPath);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in cards.EnumerateArray())
        {
            if (!c.TryGetProperty("keywords", out var kwEl))
                continue;
            foreach (var kw in kwEl.EnumerateArray())
            {
                var value = kw.GetString()!;
                if (!seen.Add(value)) continue;
                Assert.True(Enum.TryParse<Keyword>(value, true, out var parsed) && parsed != Keyword.None,
                    $"Catalog keyword '{value}' is not a known Keyword enum member.");
            }
        }
    }

    [Fact]
    public void EveryCatalogEffect_MapsToARealEffectIdAndTargetScope()
    {
        var cards = LoadArray(CardsJsonPath);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenTargets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in cards.EnumerateArray())
        {
            if (!c.TryGetProperty("effects", out var effEl))
                continue;
            foreach (var e in effEl.EnumerateArray())
            {
                var id = e.GetProperty("id").GetString()!;
                if (seenIds.Add(id))
                    Assert.True(Enum.TryParse<EffectId>(id, true, out var parsed) && parsed != EffectId.None,
                        $"Catalog effect id '{id}' is not a known EffectId enum member.");

                if (e.TryGetProperty("target", out var targetEl))
                {
                    var target = targetEl.GetString()!;
                    if (seenTargets.Add(target))
                        Assert.True(Enum.TryParse<EffectTargetScope>(target, true, out var scope) && scope != EffectTargetScope.None,
                            $"Catalog effect target '{target}' is not a known EffectTargetScope enum member.");
                }
            }
        }
    }

    [Fact]
    public void EvolutionCards_DeclareAnEvolutionOfRace()
    {
        var cards = LoadArray(CardsJsonPath);
        var evos = cards.EnumerateArray()
            .Where(c => c.TryGetProperty("cardType", out var t)
                && string.Equals(t.GetString(), "EvolutionCreature", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(evos);
        foreach (var e in evos)
            Assert.True(e.TryGetProperty("evolutionOf", out var evoEl) && !string.IsNullOrEmpty(evoEl.GetString()),
                $"Evolution {e.GetProperty("id").GetString()} must declare an 'evolutionOf' race.");
    }

    [Fact]
    public void ExplicitEvolutionOf_MatchesItsOwnRaceOrType()
    {
        var byName = LoadCards();

        // A real evolution card: opposite-type shapes may stack on either race,
        // but a plain creature evolution names one race it can sit on.
        Assert.True(byName.TryGetValue("Larba Geer, the Immaculate", out var larba));
        Assert.Equal("EvolutionCreature", larba[0].GetProperty("cardType").GetString());
        Assert.Equal("Guardian", larba[0].GetProperty("evolutionOf").GetString());
    }

    [Fact]
    public void CuratedStarterCards_CarryTheirIntendedAbilities()
    {
        var byName = LoadCards();

        var chosen = new[]
        {
            "Bolshack Dragon",
            "Super Terradragon Bailas Gale",
            "King Mazelan",
            "Tornado Flame",
            "Terror Pit",
            "Aqua Guard",
            "Fatal Attacker Horvath",
            "Spiral Gate",
            "Holy Awe",
        };

        foreach (var name in chosen)
            Assert.True(byName.ContainsKey(name), $"Curated card '{name}' is missing from cards.json.");

        string? FirstKeyword(string cardName, string keyword)
        {
            var card = byName[cardName][0];
            if (!card.TryGetProperty("keywords", out var kwEl)) return null;
            var hit = kwEl.EnumerateArray().FirstOrDefault(k => string.Equals(k.GetString(), keyword, StringComparison.Ordinal));
            return hit.ValueKind == JsonValueKind.Undefined ? null : hit.GetString();
        }

        IReadOnlyList<JsonElement> EffectsOf(string cardName)
        {
            var card = byName[cardName][0];
            var effEl = card.GetProperty("effects");
            return effEl.EnumerateArray().ToList();
        }

        Assert.Equal("DoubleBreaker", FirstKeyword("Bolshack Dragon", "DoubleBreaker"));
        Assert.Equal("DoubleBreaker", FirstKeyword("Super Terradragon Bailas Gale", "DoubleBreaker"));
        Assert.Equal("DoubleBreaker", FirstKeyword("King Mazelan", "DoubleBreaker"));
        Assert.Null(FirstKeyword("Tornado Flame", "DoubleBreaker"));

        Assert.Equal("ShieldTrigger", FirstKeyword("Terror Pit", "ShieldTrigger"));
        Assert.Equal("ShieldTrigger", FirstKeyword("Tornado Flame", "ShieldTrigger"));
        Assert.Equal("Blocker", FirstKeyword("Aqua Guard", "Blocker"));
        Assert.Equal("PowerAttacker", FirstKeyword("Fatal Attacker Horvath", "PowerAttacker"));

        Assert.Contains(EffectsOf("Terror Pit"), e => e.GetProperty("id").GetString() == "Spell_DestroyPowerAtMost");
        Assert.Contains(EffectsOf("Tornado Flame"), e => e.GetProperty("id").GetString() == "Spell_DestroyPowerAtMost"
                                                          && e.GetProperty("value").GetInt32() == 4000);
        Assert.Contains(EffectsOf("Spiral Gate"), e => e.GetProperty("id").GetString() == "Spell_ReturnToHand");
        // Holy Awe's mass-tap is unrepresentable; the mapping keeps it effect-less
        // and documents the approximation as a note instead of a wrong pseudo-effect.
        Assert.Equal("ShieldTrigger", FirstKeyword("Holy Awe", "ShieldTrigger"));
        Assert.False(EffectsOf("Holy Awe").Any());
    }
}