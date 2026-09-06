using System.Linq;
using DuelMasters.Domain;
using Xunit;

namespace DuelMasters.Domain.Tests;

/// <summary>
/// Coverage for the Phase 4 engine expansion: targeted on-play triggers, mana
/// acceleration, destruction substitutions, the new spell effects (board wipe,
/// return-up-to, ramp, discard) and the Charger keyword.
/// </summary>
public class ExpansionEffectTests
{
    private static void Mana(GameHarness h, Player owner, Civilization civ, int count = 1)
    {
        for (var i = 0; i < count; i++)
            h.PutMana(owner, CardFactory.Creature(i, 1000, civ, $"Mana-{i}"));
    }

    // ------------------------------------------------------------- on-play sets

    [Fact]
    public void OnPlay_TapCreature_TapsTheChosenTarget()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Fire);
        h.PutInHand(h.P1, CardFactory.Creature(1, 2000, Civilization.Fire, "Freezer",
            CardFactory.Eff(EffectId.OnPlay_TapCreature, EffectTargetScope.AnyCreature)));
        var victim = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "Victim"));

        h.Game.SummonCreature(0, h.P2, h.P2.BattleZone.IndexOf(victim));

        Assert.True(victim.IsTapped);
    }

    [Fact]
    public void OnPlay_TapCreature_WithoutTarget_ResolvesHarmlessly()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Fire);
        h.PutInHand(h.P1, CardFactory.Creature(1, 2000, Civilization.Fire, "Freezer",
            CardFactory.Eff(EffectId.OnPlay_TapCreature, EffectTargetScope.AnyCreature)));
        var untouched = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "Relaxing"));

        h.Game.SummonCreature(0); // no target supplied

        Assert.False(untouched.IsTapped);
    }

    [Fact]
    public void OnPlay_ReturnToHand_SendsTargetBackToItsOwnersHand()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Water);
        h.PutInHand(h.P1, CardFactory.Creature(1, 1000, Civilization.Water, "Bouncer",
            CardFactory.Eff(EffectId.OnPlay_ReturnToHand, EffectTargetScope.AnyCreature)));
        var target = h.PutCreature(h.P2, CardFactory.Creature(1, 4000, Civilization.Fire, "Hulk"));

        h.Game.SummonCreature(0, h.P2, h.P2.BattleZone.IndexOf(target));

        Assert.DoesNotContain(target, h.P2.BattleZone);
        Assert.Contains(target, h.P2.Hand);
    }

    [Fact]
    public void OnPlay_UntapOwnCreature_OnlyTargetsOwnCreatures()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Light);
        h.PutInHand(h.P1, CardFactory.Creature(1, 1000, Civilization.Light, "Healer",
            CardFactory.Eff(EffectId.OnPlay_UntapOwnCreature, EffectTargetScope.OwnCreature)));
        var enemy = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "Enemy"), tapped: true);

        Assert.Throws<RuleViolationException>(
            () => h.Game.SummonCreature(0, h.P2, h.P2.BattleZone.IndexOf(enemy)));
    }

    [Fact]
    public void OnPlay_UntapOwnCreature_UntapsTheTarget()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Light);
        h.PutInHand(h.P1, CardFactory.Creature(1, 1000, Civilization.Light, "Healer",
            CardFactory.Eff(EffectId.OnPlay_UntapOwnCreature, EffectTargetScope.OwnCreature)));
        var own = h.PutCreature(h.P1, CardFactory.Creature(1, 1000, Civilization.Light, "Sleeping"), tapped: true);

        h.Game.SummonCreature(0, h.P1, h.P1.BattleZone.IndexOf(own));

        Assert.False(own.IsTapped);
    }

    [Fact]
    public void OnPlay_UntapAllOwnCreatures_UntapsEveryOwnCreature()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Light);
        h.PutInHand(h.P1, CardFactory.Creature(1, 1000, Civilization.Light, "Awakener",
            CardFactory.Eff(EffectId.OnPlay_UntapAllOwnCreatures)));
        var a = h.PutCreature(h.P1, CardFactory.Creature(1, 1000, Civilization.Light, "A"), tapped: true);
        var b = h.PutCreature(h.P1, CardFactory.Creature(1, 1000, Civilization.Light, "B"), tapped: true);
        var enemy = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "E"), tapped: true);

        h.Game.SummonCreature(0);

        Assert.False(a.IsTapped);
        Assert.False(b.IsTapped);
        Assert.True(enemy.IsTapped); // only the owner's creatures were untapped
    }

    [Fact]
    public void OnPlay_ChargeMana_PutsTopOfDeckIntoManaZone()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Nature);
        h.PutInHand(h.P1, CardFactory.Creature(1, 1000, Civilization.Nature, "Ramp",
            CardFactory.Eff(EffectId.OnPlay_ChargeMana)));

        var before = h.P1.Deck.Count;
        var top = h.P1.Deck[0];
        h.Game.SummonCreature(0);

        Assert.Equal(before - 1, h.P1.Deck.Count);
        Assert.Contains(top, h.P1.ManaZone.Select(m => m.Card));
    }

    [Fact]
    public void OnPlay_DestroyPowerAtMost_RejectsTargetAboveThePowerLimit_BeforePaying()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Fire);
        var cre = CardFactory.Creature(1, 1000, Civilization.Fire, "Snapper",
            CardFactory.Eff(EffectId.OnPlay_DestroyPowerAtMost, EffectTargetScope.AnyCreature, value: 3000));
        h.PutInHand(h.P1, cre);
        var tough = h.PutCreature(h.P2, CardFactory.Creature(1, 5000, Civilization.Fire, "Tank"));

        Assert.Throws<RuleViolationException>(
            () => h.Game.SummonCreature(0, h.P2, h.P2.BattleZone.IndexOf(tough)));

        Assert.Single(h.P1.Hand);
        Assert.All(h.P1.ManaZone, m => Assert.False(m.IsTapped));
        Assert.Contains(tough, h.P2.BattleZone);
    }

    // ------------------------------------------------------ destroyed substitutes

    [Fact]
    public void OnDestroyed_ToHand_SubstitutesTheGraveyardTrip()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var defender = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Darkness, "Phoenix",
            CardFactory.Eff(EffectId.OnDestroyed_ToHand)), tapped: true);
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Fire, "Giant"));

        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(defender));

        Assert.DoesNotContain(defender, h.P2.BattleZone);
        Assert.DoesNotContain(defender, h.P2.Graveyard);
        Assert.Contains(defender, h.P2.Hand);
    }

    [Fact]
    public void OnDestroyed_ToMana_SubstitutesTheGraveyardTrip()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var defender = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Nature, "Weeder",
            CardFactory.Eff(EffectId.OnDestroyed_ToMana)), tapped: true);
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Fire, "Giant"));

        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(defender));

        Assert.DoesNotContain(defender, h.P2.BattleZone);
        Assert.DoesNotContain(defender, h.P2.Graveyard);
        Assert.Contains(defender, h.P2.ManaZone);
    }

    // ---------------------------------------------------------- spell effects

    [Fact]
    public void DestroyAllSpell_WipesEveryCreatureOnBothSides()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Fire, 6);
        var spell = CardFactory.Spell(6, Civilization.Fire, "Apocalypse",
            CardFactory.Eff(EffectId.Spell_DestroyAllCreatures));
        h.PutInHand(h.P1, spell);
        var own = h.PutCreature(h.P1, CardFactory.Creature(1, 5000, Civilization.Fire, "Own"));
        var theirs = h.PutCreature(h.P2, CardFactory.Creature(1, 5000, Civilization.Fire, "Theirs"));

        h.Game.CastSpell(0);

        Assert.Empty(h.P1.BattleZone);
        Assert.Empty(h.P2.BattleZone);
        Assert.Contains(own, h.P1.Graveyard);
        Assert.Contains(theirs, h.P2.Graveyard);
    }

    [Fact]
    public void ReturnUpToSpell_ReturnsSeveralCreaturesToTheirOwnersHands()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Water, 5);
        var spell = CardFactory.Spell(5, Civilization.Water, "Tidal Wave",
            CardFactory.Eff(EffectId.Spell_ReturnUpToToHand, EffectTargetScope.AnyCreature, value: 2));
        h.PutInHand(h.P1, spell);
        var mine = h.PutCreature(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "Mine"));
        var theirs = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "Theirs"));

        h.Game.CastSpell(0, new SpellTarget[]
        {
            new(h.P2, h.P2.BattleZone.IndexOf(theirs)),
            new(h.P1, h.P1.BattleZone.IndexOf(mine)),
        });

        Assert.DoesNotContain(mine, h.P1.BattleZone);
        Assert.DoesNotContain(theirs, h.P2.BattleZone);
        Assert.Contains(mine, h.P1.Hand);
        Assert.Contains(theirs, h.P2.Hand);
    }

    [Fact]
    public void ChargeManaSpell_RampsManaFromTheDeck()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Nature, 2);
        var spell = CardFactory.Spell(2, Civilization.Nature, "Mana Nexus",
            CardFactory.Eff(EffectId.Spell_ChargeMana));
        h.PutInHand(h.P1, spell);

        var before = h.P1.Deck.Count;
        var top = h.P1.Deck[0];
        h.Game.CastSpell(0);

        Assert.Equal(before - 1, h.P1.Deck.Count);
        Assert.Contains(top, h.P1.ManaZone.Select(m => m.Card));
    }

    [Fact]
    public void DiscardRandomSpell_DiscardsThatManyOpponentCards()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Darkness, 3);
        var spell = CardFactory.Spell(3, Civilization.Darkness, "Mind Drain",
            CardFactory.Eff(EffectId.Spell_DiscardRandom, value: 2));
        h.PutInHand(h.P1, spell);
        var c1 = h.PutInHand(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "Card1"));
        var c2 = h.PutInHand(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "Card2"));

        h.Game.CastSpell(0);

        Assert.Empty(h.P2.Hand);
        Assert.Contains(c1, h.P2.Graveyard);
        Assert.Contains(c2, h.P2.Graveyard);
    }

    // -------------------------------------------------------------- charger

    [Fact]
    public void ChargerSpell_GoesToManaZoneInsteadOfGraveyard()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Nature, 4);
        var spell = CardFactory.Spell(4, Civilization.Nature, "Mana Crisis",
            CardFactory.Eff(EffectId.Spell_Draw, value: 1), Keyword.Charger);
        h.PutInHand(h.P1, spell);

        var before = h.P1.Deck.Count;
        var cast = h.Game.CastSpell(0);

        Assert.DoesNotContain(cast, h.P1.Graveyard);
        Assert.Contains(cast, h.P1.ManaZone);
        Assert.False(cast.IsTapped);
        Assert.Equal(before - 1, h.P1.Deck.Count);
    }

    [Fact]
    public void ChargerSpell_ProducedMana_IsUsableInTheSameTurn()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Nature, 3);
        var spell = CardFactory.Spell(2, Civilization.Nature, "Mana Crisis",
            CardFactory.Eff(EffectId.Spell_Draw, value: 1), Keyword.Charger);
        h.PutInHand(h.P1, spell);
        var cre = CardFactory.Creature(2, 3000, Civilization.Nature, "Ramper");
        h.PutInHand(h.P1, cre);

        h.Game.CastSpell(0); // 2 mana spent, spell becomes an untapped 3rd mana
        h.Game.SummonCreature(0); // the 2-cost creature now fits

        Assert.Contains(cre.Id, h.P1.BattleZone.Select(x => x.Card.Id));
    }
}