using System;
using System.Linq;
using DuelMasters.Domain;
using DuelMasters.Domain.Ai;
using DuelMasters.Domain.Networking;
using Xunit;

namespace DuelMasters.Domain.Tests;

/// <summary>
/// Coverage for the activated Tap Ability family: the activation gates (main
/// phase, owner's turn, untapped, not summoning-sick, before any attack, no
/// Shield Trigger window), the tapping-as-cost interaction, and every Phase 1
/// tap effect (draw, bounce, tap, destroy, temporary keyword grants, charge,
/// discard).
/// </summary>
public class TapAbilityTests
{
    private static int _serial;

    /// <summary>Build a named tap creature with one ability.</summary>
    private static Card TapCard(CardEffect eff, int cost = 3, int power = 3000, Civilization civ = Civilization.Water)
        => CardFactory.TapCreature(cost, power, civ, $"Tap-{++_serial}", eff);

    private static Card TapCard(string name, CardEffect eff, int cost = 3, int power = 3000, Civilization civ = Civilization.Water)
        => CardFactory.TapCreature(cost, power, civ, $"Tap-{name}-{++_serial}", eff);

    // ---------------------------------------------------------------- gates

    [Fact]
    public void SummoningSick_Creature_CannotUseTapAbility()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var cre = h.PutCreature(h.P1, CardFactory.TapCreature(1, 3000, Civilization.Water, "Sick", CardFactory.Eff(EffectId.Tap_Draw, value: 1)), sick: true);

        Assert.False(h.Game.CanUseTapAbility(h.P1, h.P1.BattleZone.IndexOf(cre)));
        var ex = Assert.Throws<RuleViolationException>(() => h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(cre)));
        Assert.Contains("summoning sickness", ex.Message);
    }

    [Fact]
    public void TappedCreature_CannotUseTapAbility()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var cre = h.PutCreature(h.P1, CardFactory.TapCreature(1, 3000, Civilization.Water, "Tired", CardFactory.Eff(EffectId.Tap_Draw, value: 1)), tapped: true);

        Assert.False(h.Game.CanUseTapAbility(h.P1, h.P1.BattleZone.IndexOf(cre)));
    }

    [Fact]
    public void Opponent_CannotUseOwnersTapAbility()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var cre = h.PutCreature(h.P1, CardFactory.TapCreature(1, 3000, Civilization.Water, "Mine", CardFactory.Eff(EffectId.Tap_Draw, value: 1)));

        Assert.True(h.Game.CanUseTapAbility(h.P1, h.P1.BattleZone.IndexOf(cre)));
        Assert.False(h.Game.CanUseTapAbility(h.P2, h.P1.BattleZone.IndexOf(cre)));
        var ex = Assert.Throws<RuleViolationException>(() => h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(cre), h.P2, null));
        Assert.Contains("own creatures", ex.Message);
    }

    [Fact]
    public void CannotUseTapAbility_AfterACreatureHasAttacked()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var tapCre = h.PutCreature(h.P1, CardFactory.TapCreature(1, 3000, Civilization.Water, "Mage", CardFactory.Eff(EffectId.Tap_Draw, value: 1)));
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Fire, "Attacker"));
        var victim = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Nature, "Victim"), tapped: true);

        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(victim));

        Assert.False(h.Game.CanUseTapAbility(h.P1, h.P1.BattleZone.IndexOf(tapCre)));
        var ex = Assert.Throws<RuleViolationException>(() => h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(tapCre)));
        Assert.Contains("after a creature has attacked", ex.Message);
    }

    [Fact]
    public void UsingTapAbility_TapsTheCreature_ButDoesNotLockOtherAttacks()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var mage = h.PutCreature(h.P1, CardFactory.TapCreature(1, 3000, Civilization.Water, "Mage", CardFactory.Eff(EffectId.Tap_Draw, value: 1)));
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Fire, "Attacker"));
        var victim = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Nature, "Victim"), tapped: true);

        h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(mage));

        // Tapping is the cost: the creature is no longer usable, but the "has
        // attacked" lock is untouched and a different creature may still attack.
        Assert.True(mage.IsTapped);
        Assert.False(h.Game.CanUseTapAbility(h.P1, h.P1.BattleZone.IndexOf(mage)));
        Assert.False(h.Game.HasAttackedThisTurn);
        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(victim));
        Assert.Contains(attacker, h.P1.BattleZone);
        Assert.True(attacker.IsTapped);
        Assert.True(h.Game.HasAttackedThisTurn);
    }

    [Fact]
    public void CannotUseTapAbility_WhileShieldTriggerWindowIsOpen()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var mage = h.PutCreature(h.P1, CardFactory.TapCreature(1, 3000, Civilization.Water, "Mage", CardFactory.Eff(EffectId.Tap_Draw, value: 1)));
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Fire, "Attacker"));
        var trigger = CardFactory.Spell(1, Civilization.Light, "Burst", CardFactory.Eff(EffectId.Spell_Draw, value: 1), Keyword.ShieldTrigger);
        h.SetShields(h.P2, trigger, CardFactory.Creature(1, 1000, Civilization.Nature, "Plain"));

        h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(attacker));
        Assert.True(h.Game.ShieldTriggerWindowActive);

        Assert.False(h.Game.CanUseTapAbility(h.P1, h.P1.BattleZone.IndexOf(mage)));
        h.Game.DeclineShieldTriggers();
    }

    // ------------------------------------------------------------- effects

    [Fact]
    public void TapDraw_DrawsTheDeclaredAmount()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var cre = h.PutCreature(h.P1, TapCard("Neon", CardFactory.Eff(EffectId.Tap_Draw, value: 2)));

        var deckBefore = h.P1.Deck.Count;
        var handBefore = h.P1.Hand.Count;
        h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(cre));

        Assert.Equal(deckBefore - 2, h.P1.Deck.Count);
        Assert.Equal(handBefore + 2, h.P1.Hand.Count);
    }

    [Fact]
    public void TapChargeMana_PutsTopCardOfDeckIntoManaZone()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var cre = h.PutCreature(h.P1, TapCard("Popple", CardFactory.Eff(EffectId.Tap_ChargeMana)));

        var top = h.P1.Deck[0];
        var manaBefore = h.P1.ManaZone.Count;
        h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(cre));

        Assert.Equal(manaBefore + 1, h.P1.ManaZone.Count);
        Assert.Same(top, h.P1.ManaZone[^1].Card);
        Assert.Equal(top.Id, h.P1.ManaZone[^1].Card.Id);
    }

    [Fact]
    public void TapDiscardRandom_MakesOpponentDiscardFromHandToGraveyard()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var cre = h.PutCreature(h.P1, TapCard("Crath", CardFactory.Eff(EffectId.Tap_DiscardRandom, value: 2), civ: Civilization.Darkness));
        for (var i = 0; i < 3; i++)
            h.PutInHand(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, $"P2Hand-{i}"));

        var handBefore = h.P2.Hand.Count;
        var graveBefore = h.P2.Graveyard.Count;
        h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(cre));

        Assert.Equal(handBefore - 2, h.P2.Hand.Count);
        Assert.Equal(graveBefore + 2, h.P2.Graveyard.Count);
    }

    [Fact]
    public void TapReturnToHand_ReturnsOwnCreature()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var cre = h.PutCreature(h.P1, TapCard("Aero", CardFactory.Eff(EffectId.Tap_ReturnToHand, EffectTargetScope.AnyCreature)));
        var target = h.PutCreature(h.P1, CardFactory.Creature(1, 1000, Civilization.Nature, "MyGuy"));

        var idx = h.P1.BattleZone.IndexOf(cre);
        var targetIdx = h.P1.BattleZone.IndexOf(target);
        h.Game.ActivateTapAbility(idx, new[] { new SpellTarget(h.P1, targetIdx) });

        Assert.DoesNotContain(target, h.P1.BattleZone);
        Assert.Contains(target, h.P1.Hand);
        Assert.True(cre.IsTapped);
    }

    [Fact]
    public void TapReturnToHand_ReturnsOpponentsCreature()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var cre = h.PutCreature(h.P1, TapCard("Aero", CardFactory.Eff(EffectId.Tap_ReturnToHand, EffectTargetScope.AnyCreature)));
        var target = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Nature, "TheirGuy"));

        h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(cre), new[] { new SpellTarget(h.P2, h.P2.BattleZone.IndexOf(target)) });

        Assert.DoesNotContain(target, h.P2.BattleZone);
        Assert.Contains(target, h.P2.Hand);
    }

    [Fact]
    public void TapTapOpponentCreature_TapsIt()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var cre = h.PutCreature(h.P1, TapCard("Chen", CardFactory.Eff(EffectId.Tap_TapOpponentCreature, EffectTargetScope.OpponentCreature)));
        var target = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "ReadyGuy"));

        h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(cre), new[] { new SpellTarget(h.P2, h.P2.BattleZone.IndexOf(target)) });

        Assert.True(target.IsTapped);
    }

    [Fact]
    public void TapReturnSpellFromManaZone_OnlySpellsAreTargetable()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var cre = h.PutCreature(h.P1, TapCard("Cosmo", CardFactory.Eff(EffectId.Tap_ReturnSpellFromManaToHand, EffectTargetScope.OwnManaZone)));
        var spell = h.PutMana(h.P1, CardFactory.Spell(1, Civilization.Water, "Bubble", CardFactory.Eff(EffectId.Spell_Draw, value: 1)));
        var critter = h.PutMana(h.P1, CardFactory.Creature(1, 1000, Civilization.Water, "ManaCreature"));

        var eff = cre.Card.TapAbilities[0];
        Assert.DoesNotContain(critter, h.Game.TapTargetPool(h.P1, eff));
        Assert.Contains(spell, h.Game.TapTargetPool(h.P1, eff));

        h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(cre), new[] { new SpellTarget(h.P1, h.P1.ManaZone.IndexOf(spell)) });

        Assert.DoesNotContain(spell, h.P1.ManaZone);
        Assert.Contains(spell, h.P1.Hand);
        Assert.DoesNotContain(critter, h.P1.Hand);
    }

    [Fact]
    public void TapReturnCreatureFromManaZone_ReturnsCreatureToHand()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var cre = h.PutCreature(h.P1, TapCard("Brood", CardFactory.Eff(EffectId.Tap_ReturnCreatureFromManaToHand, EffectTargetScope.OwnManaZone)));
        var target = h.PutMana(h.P1, CardFactory.Creature(1, 1000, Civilization.Nature, "ManaGuy"));
        var spell = h.PutMana(h.P1, CardFactory.Spell(1, Civilization.Water, "SpellCard"));

        var eff = cre.Card.TapAbilities[0];
        Assert.DoesNotContain(spell, h.Game.TapTargetPool(h.P1, eff));

        h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(cre), new[] { new SpellTarget(h.P1, h.P1.ManaZone.IndexOf(target)) });

        Assert.DoesNotContain(target, h.P1.ManaZone);
        Assert.Contains(target, h.P1.Hand);
    }

    [Fact]
    public void TapReturnOpponentsManaCard_ReturnsItToHisHand()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var cre = h.PutCreature(h.P1, TapCard("Fencer", CardFactory.Eff(EffectId.Tap_ReturnManaCardToHand, EffectTargetScope.OpponentManaZone)));
        var target = h.PutMana(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "TheirMana"));

        h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(cre), new[] { new SpellTarget(h.P2, h.P2.ManaZone.IndexOf(target)) });

        Assert.DoesNotContain(target, h.P2.ManaZone);
        Assert.Contains(target, h.P2.Hand);
    }

    [Fact]
    public void TapReturnGraveyardCreature_OnlyMatchesTheCivilization()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var cre = h.PutCreature(h.P1, TapCard("GrimSoul", CardFactory.Eff(EffectId.Tap_ReturnGraveCreatureToHand, EffectTargetScope.OwnGraveyard, data: "Darkness")));
        var dark = h.PutCreature(h.P1, CardFactory.Creature(1, 1000, Civilization.Darkness, "DarkDead"), tapped: true);
        var fire = h.PutCreature(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "FireDead"), tapped: true);
        h.P1.BattleZone.Remove(dark);
        h.P1.Graveyard.Add(dark);
        h.P1.BattleZone.Remove(fire);
        h.P1.Graveyard.Add(fire);

        var eff = cre.Card.TapAbilities[0];
        Assert.Contains(dark, h.Game.TapTargetPool(h.P1, eff));
        Assert.DoesNotContain(fire, h.Game.TapTargetPool(h.P1, eff));

        h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(cre), new[] { new SpellTarget(h.P1, h.P1.Graveyard.IndexOf(dark)) });

        Assert.Contains(dark, h.P1.Hand);
        Assert.Contains(fire, h.P1.Graveyard);
    }

    [Fact]
    public void TapDestroyPowerAtMost_DestroysOnlyWeakCreatures()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var cre = h.PutCreature(h.P1, TapCard("Kipo", CardFactory.Eff(EffectId.Tap_DestroyPowerAtMost, EffectTargetScope.OpponentCreature, value: 2000)));
        var weak = h.PutCreature(h.P2, CardFactory.Creature(1, 2000, Civilization.Nature, "Weak"));
        var strong = h.PutCreature(h.P2, CardFactory.Creature(1, 6000, Civilization.Nature, "Strong"));

        var eff = cre.Card.TapAbilities[0];
        Assert.Contains(weak, h.Game.TapTargetPool(h.P1, eff));
        Assert.DoesNotContain(strong, h.Game.TapTargetPool(h.P1, eff));

        h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(cre), new[] { new SpellTarget(h.P2, h.P2.BattleZone.IndexOf(weak)) });

        Assert.Contains(weak, h.P2.Graveyard);
        Assert.Contains(strong, h.P2.BattleZone);
    }

    [Fact]
    public void TapDestroyBlocker_DestroysOnlyBlockers()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var cre = h.PutCreature(h.P1, TapCard("Rikabu", CardFactory.Eff(EffectId.Tap_DestroyBlocker, EffectTargetScope.OpponentCreature)));
        var blocker = h.PutCreature(h.P2, CardFactory.Creature(1, 2000, Civilization.Light, "Guard", Keyword.Blocker));
        var plain = h.PutCreature(h.P2, CardFactory.Creature(1, 2000, Civilization.Nature, "Plain"));

        var eff = cre.Card.TapAbilities[0];
        Assert.Contains(blocker, h.Game.TapTargetPool(h.P1, eff));
        Assert.DoesNotContain(plain, h.Game.TapTargetPool(h.P1, eff));

        h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(cre), new[] { new SpellTarget(h.P2, h.P2.BattleZone.IndexOf(blocker)) });

        Assert.Contains(blocker, h.P2.Graveyard);
        Assert.Contains(plain, h.P2.BattleZone);
    }

    [Fact]
    public void TapBoostPower_TemporarilyRaisesPowerUntilEndOfTurn()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var cre = h.PutCreature(h.P1, TapCard("Bandit", CardFactory.Eff(EffectId.Tap_BoostPowerEot, EffectTargetScope.OwnCreature, value: 5000)));
        var partner = h.PutCreature(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "Partner"));

        h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(cre), new[] { new SpellTarget(h.P1, h.P1.BattleZone.IndexOf(partner)) });

        Assert.Equal(5000, partner.TempPower);

        h.Game.EndMainPhase();
        h.Game.EndTurn();
        Assert.Equal(0, partner.TempPower);
    }

    [Fact]
    public void TapGrantUnblockableEot_MakesTargetUnblockableUntilEndOfTurn()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var cre = h.PutCreature(h.P1, TapCard("Sopian", CardFactory.Eff(EffectId.Tap_GrantUnblockableEot, EffectTargetScope.OwnCreature)));
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 5000, Civilization.Fire, "Assassin"));
        h.PutCreature(h.P2, CardFactory.Creature(1, 2000, Civilization.Light, "Guard", Keyword.Blocker));

        h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(cre), new[] { new SpellTarget(h.P1, h.P1.BattleZone.IndexOf(attacker)) });

        Assert.True(attacker.HasKeywordNow(Keyword.Unblockable));
        Assert.Equal(0, h.Game.ReadyBlockerChoices(h.P1.BattleZone.IndexOf(attacker)));

        h.Game.EndMainPhase();
        h.Game.EndTurn();
        h.Game.StartTurn();
        Assert.False(attacker.HasKeywordNow(Keyword.Unblockable));
    }

    [Fact]
    public void TapGrantUnblockableCivEot_AppliesToEveryMatchingCivilizationCreature()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var cre = h.PutCreature(h.P1, TapCard("Benthos", CardFactory.Eff(EffectId.Tap_GrantUnblockableCivEot, data: "Water"), civ: Civilization.Water));
        var water = h.PutCreature(h.P1, CardFactory.Creature(1, 5000, Civilization.Water, "WaterFriend"));
        var fire = h.PutCreature(h.P1, CardFactory.Creature(1, 5000, Civilization.Fire, "FireFriend"));
        h.PutCreature(h.P2, CardFactory.Creature(1, 2000, Civilization.Light, "Guard", Keyword.Blocker));

        h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(cre));

        Assert.True(water.HasKeywordNow(Keyword.Unblockable));
        Assert.False(fire.HasKeywordNow(Keyword.Unblockable));
        Assert.Equal(0, h.Game.ReadyBlockerChoices(h.P1.BattleZone.IndexOf(water)));
        Assert.Equal(1, h.Game.ReadyBlockerChoices(h.P1.BattleZone.IndexOf(fire)));
    }

    [Fact]
    public void TapGrantCanAttackUntappedCivEot_LetsMatchingCreaturesAttackTappedTargets()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var cre = h.PutCreature(h.P1, TapCard("Galiacruse", CardFactory.Eff(EffectId.Tap_GrantCanAttackUntappedCivEot, data: "Fire"), civ: Civilization.Fire));
        var fire = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Fire, "FireBrawler"));
        var nature = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Nature, "NatureScout"));
        var ready = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Nature, "ReadyGuy"));

        h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(cre));

        // Fire creatures may attack the untapped target; nature ones still may not.
        var fireIdx = h.P1.BattleZone.IndexOf(fire);
        h.Game.AttackCreature(fireIdx, h.P2.BattleZone.IndexOf(ready));
        Assert.DoesNotContain(ready, h.P2.BattleZone);

        var natureIdx = h.P1.BattleZone.IndexOf(nature);
        Assert.Throws<RuleViolationException>(() => h.Game.AttackCreature(natureIdx, 0));
    }

    [Fact]
    public void TapGrantDoubleBreakerEot_MakesMatchingCreatureBreakTwoShields()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var cre = h.PutCreature(h.P1, TapCard("Migasa", CardFactory.Eff(EffectId.Tap_GrantDoubleBreakerEot, EffectTargetScope.OwnCreature, data: "Fire"), civ: Civilization.Darkness));
        var fire = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Fire, "FireDragon"));
        var nature = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Nature, "NatureDragon"));
        h.SetShields(h.P2, CardFactory.Creature(1, 1000, Civilization.Light, "S1"),
                         CardFactory.Creature(1, 1000, Civilization.Light, "S2"),
                         CardFactory.Creature(1, 1000, Civilization.Light, "S3"));

        var eff = cre.Card.TapAbilities[0];
        Assert.Contains(fire, h.Game.TapTargetPool(h.P1, eff));
        Assert.DoesNotContain(nature, h.Game.TapTargetPool(h.P1, eff));

        h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(cre), new[] { new SpellTarget(h.P1, h.P1.BattleZone.IndexOf(fire)) });
        h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(fire));

        Assert.Equal(1, h.P2.ShieldCount);
    }

    [Fact]
    public void TapGrantSlayerEot_DestroysBothCreaturesInABattle()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var cre = h.PutCreature(h.P1, TapCard("Lupa", CardFactory.Eff(EffectId.Tap_GrantSlayerEot, EffectTargetScope.OwnCreature)));
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 1000, Civilization.Darkness, "Killer"));
        var victim = h.PutCreature(h.P2, CardFactory.Creature(1, 4000, Civilization.Light, "Equal"), tapped: true);

        h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(cre), new[] { new SpellTarget(h.P1, h.P1.BattleZone.IndexOf(attacker)) });
        Assert.True(attacker.HasKeywordNow(Keyword.Slayer));

        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(victim));

        // Slayer destroys the opponent even when the slayer loses the battle, so
        // a 1000-power slayer facing a 4000-power defender kills both.
        Assert.Contains(attacker, h.P1.Graveyard);
        Assert.Contains(victim, h.P2.Graveyard);
    }

    [Fact]
    public void TapGrantSpeedAttackerEot_EmitsKeywordUntilEndOfTurn()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var cre = h.PutCreature(h.P1, TapCard("Lizard", CardFactory.Eff(EffectId.Tap_GrantSpeedAttackerEot, EffectTargetScope.OwnCreature), civ: Civilization.Fire));
        var friend = h.PutCreature(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "Friend"));

        h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(cre), new[] { new SpellTarget(h.P1, h.P1.BattleZone.IndexOf(friend)) });

        Assert.True(friend.HasKeywordNow(Keyword.SpeedAttacker));

        h.Game.EndMainPhase();
        h.Game.EndTurn();
        Assert.False(friend.HasKeywordNow(Keyword.SpeedAttacker));
    }

    [Fact]
    public void NotModelledTapAbility_IsNeverUsable()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var cre = h.PutCreature(h.P1, CardFactory.TapCreature(1, 3000, Civilization.Water, "Deferred"));
        Assert.Equal(EffectId.Tap_NotModelled, cre.Card.TapAbilities[0].Id);

        Assert.False(h.Game.CanUseTapAbility(h.P1, h.P1.BattleZone.IndexOf(cre)));
        var ex = Assert.Throws<RuleViolationException>(() => h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(cre)));
        Assert.Contains("not modelled", ex.Message);
        Assert.False(cre.IsTapped);
    }

    // --------------------------------------------------- networked state / AI

    [Fact]
    public void DuelGameState_AnnotatesGlobalTapAbilityForViewer()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var neo = h.PutCreature(h.P1, TapCard("Neon", CardFactory.Eff(EffectId.Tap_Draw, value: 2)));

        var state = DuelGameState.From(h.Game, "AAAAAA", DuelSide.Player1);

        var cs = state.Players.Single(p => p.Side == DuelSide.Player1).BattleZone.Single();
        Assert.True(cs.HasTapAbility);
        Assert.True(cs.CanUseTapAbility);
        Assert.Null(cs.TapAbilityTargets);
    }

    [Fact]
    public void DuelGameState_AnnotatesTapTargetsForTargetedAbility()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var friend = h.PutCreature(h.P1, CardFactory.Creature(1, 1000, Civilization.Nature, "Friend"));
        var bandit = h.PutCreature(h.P1, TapCard("Bandit", CardFactory.Eff(EffectId.Tap_BoostPowerEot, EffectTargetScope.OwnCreature, value: 5000)));

        var state = DuelGameState.From(h.Game, "AAAAAA", DuelSide.Player1);

        var cs = state.Players.Single(p => p.Side == DuelSide.Player1).BattleZone.Single(c => c.InstanceId == "Player1:B:1");
        Assert.True(cs.HasTapAbility);
        Assert.True(cs.CanUseTapAbility);
        Assert.Equal(2, cs.TapAbilityTargets!.Count); // the friend and the bandit itself
        var pick = cs.TapAbilityTargets.Single(t => t.Index == 0);
        Assert.Equal(DuelSide.Player1, pick.Side);
        Assert.Contains("Friend", pick.Label);
    }

    [Fact]
    public void AiController_UsesGlobalTapAbilityBeforeAttacking()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var neo = h.PutCreature(h.P1, TapCard("Neon-AI", CardFactory.Eff(EffectId.Tap_Draw, value: 2), cost: 5, power: 3000));
        var handBefore = h.P1.Hand.Count;

        new AiController(h.P1).PlayTurn(h.Game);

        // The AI activates global draw abilities before its attacks, paying the tap.
        Assert.True(neo.IsTapped);
        Assert.True(h.P1.Hand.Count >= handBefore);
        Assert.True(h.P1.ManaZone.Count >= 1);
    }
}