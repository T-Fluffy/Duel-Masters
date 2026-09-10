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
            foreach (var effEl in new[] { "effects", "tapAbilities" })
            {
                if (!c.TryGetProperty(effEl, out var prop) || prop.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var e in prop.EnumerateArray())
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
    }

    [Fact]
    public void TapAbilityCards_CarryModelledOrDeferredAbilities()
    {
        var byName = LoadCards();

        // Exactly the known Tap Ability set is present in the catalog.
        var tapCards = byName.Values
            .SelectMany(list => list)
            .Where(c => c.TryGetProperty("tapAbilities", out var ta) && ta.ValueKind == JsonValueKind.Array && ta.GetArrayLength() > 0)
            .ToList();

        // Phase 1: modelled tap abilities (resolvable by the engine).
        var modelled = new Dictionary<string, string>
        {
            ["Aeropica"] = "Tap_ReturnToHand",
            ["Chen Treg, Vizier of Blades"] = "Tap_TapOpponentCreature",
            ["Cosmogold, Spectral Knight"] = "Tap_ReturnSpellFromManaToHand",
            ["Neon Cluster"] = "Tap_Draw",
            ["Sopian"] = "Tap_GrantUnblockableEot",
            ["Grim Soul, Shadow of Reversal"] = "Tap_ReturnGraveCreatureToHand",
            ["Lupa, Poison-Tipped Doll"] = "Tap_GrantSlayerEot",
            ["Legionnaire Lizard"] = "Tap_GrantSpeedAttackerEot",
            ["Migasa, Adept of Chaos"] = "Tap_GrantDoubleBreakerEot",
            ["Rikabu's Screwdriver"] = "Tap_DestroyBlocker",
            ["Mighty Bandit, Ace of Thieves"] = "Tap_BoostPowerEot",
            ["King Benthos"] = "Tap_GrantUnblockableCivEot",
            ["Armored Transport Galiacruse"] = "Tap_GrantCanAttackUntappedCivEot",
            ["Aqua Fencer"] = "Tap_ReturnManaCardToHand",
            ["Biancus"] = "Tap_GrantUnblockableEot",
            ["Kipo's Contraption"] = "Tap_DestroyPowerAtMost",
            ["Brood Shell"] = "Tap_ReturnCreatureFromManaToHand",
            ["Popple, Flowerpetal Dancer"] = "Tap_ChargeMana",
            ["Crath Lade, Merciless King"] = "Tap_DiscardRandom",
            // Slice A: end-of-turn untap / race-choosing effects.
            ["Gandar, Seeker of Explosions"] = "Tap_UntapOwnCivEot",
            ["Tra Rion, Penumbra Guardian"] = "Tap_ChooseRaceUntapEot",
            ["Hokira"] = "Tap_ChooseRaceToHandEot",
            ["Venom Worm"] = "Tap_ChooseRaceGrantSlayerEot",
            // Slice B: multi-target mana moves (untargeted global effects).
            ["Bliss Totem, Avatar of Luck"] = "Tap_GraveToMana",
            ["Tangle Fist, the Weaver"] = "Tap_HandToMana",
            ["Sky Crusher, the Agitator"] = "Tap_ManaToGrave",
            // Slice A2: combat-hook effects (power/breaker riders, must-attack, blocking gates).
            ["Battleship Mutant"] = "Tap_GrantOwnCivPowerDoubleBreakerDestroyEot",
            ["Gigio's Hammer"] = "Tap_ChooseRaceMustAttackPowerAttackerEot",
            ["Silvermoon Trailblazer"] = "Tap_ChooseRaceUnblockableByPowerEot",
            // Slice C: opponent-sacrifice / shield riders / deck searches.
            ["Tank Mutant"] = "Tap_OpponentDestroysOwnCreature",
            ["Spinning Totem"] = "Tap_BlockBreaksShieldEot",
            ["Rondobil, the Explorer"] = "Tap_AddOwnCreatureToShields",
            ["Charmilia, the Enticer"] = "Tap_DeckSearchCreatureToHand",
            ["Kachua, Keeper of the Icegate"] = "Tap_DeckSearchDragonSummonEotDestroy",
            // Final slice: decision effects (shield-look and scry reorder).
            ["Adomis, the Oracle"] = "Tap_ChooseShieldLook",
            ["Garatyano"] = "Tap_ScryTopCards",
        };

        foreach (var (name, effId) in modelled)
            Assert.True(byName.TryGetValue(name, out var list) && list.Any(c =>
                    c.TryGetProperty("tapAbilities", out var ta)
                    && ta.EnumerateArray().Any(e => e.GetProperty("id").GetString() == effId)),
                $"Modelled tap ability missing: {name} ({effId}).");

        // Deferred tap abilities must be explicitly marked with the placeholder
        // effect; every other tap ability must be one of the modelled set.
        var modelledIds = new HashSet<string>(modelled.Values, StringComparer.Ordinal)
        {
            EffectId.Tap_NotModelled.ToString(),
        };
        foreach (var c in tapCards)
        {
            foreach (var e in c.GetProperty("tapAbilities").EnumerateArray())
            {
                var id = e.GetProperty("id").GetString()!;
                Assert.True(modelledIds.Contains(id),
                    $"Tap ability '{id}' on '{c.GetProperty("id").GetString()}' is neither modelled nor deferred.");
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

    [Fact]
    public void Dm08TurboRushSlice_CarriesItsIntendedAbilities()
    {
        var byName = LoadCards();

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
            if (!card.TryGetProperty("effects", out var effEl)) return System.Array.Empty<JsonElement>();
            return effEl.EnumerateArray().ToList();
        }

        // Magmadragon Jagalzor: the Turbo Rush aura granting Speed Attacker to all own creatures.
        Assert.Contains(EffectsOf("Magmadragon Jagalzor"),
            e => e.GetProperty("id").GetString() == "StaticTurbo_SpeedAttackerAll");

        // Solar Grass, Gigaclaws, Carbonite Scarab: attack / unblocked / blocked triggers.
        Assert.Contains(EffectsOf("Solar Grass"),
            e => e.GetProperty("id").GetString() == "AttackTrigger_UntapAllOwnExceptSelf");
        Assert.Contains(EffectsOf("Gigaclaws"),
            e => e.GetProperty("id").GetString() == "AttackTrigger_OpponentDiscardsHand");
        Assert.Contains(EffectsOf("Carbonite Scarab"),
            e => e.GetProperty("id").GetString() == "BlockedTrigger_BreakOneShield");

        // Illusion Fish: keyword-only unblockable.
        Assert.Equal("Unblockable", FirstKeyword("Illusion Fish", "Unblockable"));

        // Missile Soldier Ultimo: can attack untapped creatures and has Power Attacker +4000.
        Assert.Equal("CanAttackUntappedCreatures", FirstKeyword("Missile Soldier Ultimo", "CanAttackUntappedCreatures"));
        Assert.Equal("PowerAttacker", FirstKeyword("Missile Soldier Ultimo", "PowerAttacker"));
        Assert.Contains(EffectsOf("Missile Soldier Ultimo"), e =>
            e.GetProperty("id").GetString() == "PowerAttacker_AttackBoost"
            && e.GetProperty("value").GetInt32() == 4000);

        // Senia, Orchard Avenger: always +5000 power and Double Breaker.
        Assert.Equal("DoubleBreaker", FirstKeyword("Senia, Orchard Avenger", "DoubleBreaker"));
        Assert.Contains(EffectsOf("Senia, Orchard Avenger"), e =>
            e.GetProperty("id").GetString() == "StaticPower_AlwaysBoost"
            && e.GetProperty("value").GetInt32() == 5000);
    }
}