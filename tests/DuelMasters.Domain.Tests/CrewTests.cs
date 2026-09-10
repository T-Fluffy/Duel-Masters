using System;
using System.Linq;
using DuelMasters.Domain;
using DuelMasters.Domain.Ai;
using Xunit;

namespace DuelMasters.Domain.Tests;

/// <summary>
/// Coverage for the DM-06 "Crew" mechanic (Arc Bine, Fort Megacluster, Living
/// Citadel Vosh): the tap ability's payer set widens to every own creature of the
/// ability's civilization. The ordinary tap-ability timing rules still apply and
/// the tapped payer gives up its attack for the turn.
/// </summary>
public class CrewTests
{
    private static int _serial;

    private static Card CrewCard(string name, string crewCiv, CardEffect ability, Civilization civ = Civilization.Water)
        => CardFactory.CrewCard(4, 2000, civ, $"{name}-{++_serial}", crewCiv, ability);

    private static Card Creature(Civilization civ, string name, int power = 2000)
        => CardFactory.Creature(1, power, civ, $"{name}-{++_serial}");

    // ------------------------------------------------------- payer selection

    [Fact]
    public void Crew_AnotherClanCreature_PaysTheTapAndTheEffectResolves()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var fortress = h.PutCreature(h.P1, CrewCard("Fortress", "Water",
            CardFactory.Eff(EffectId.Tap_Draw, value: 1)));
        var helper = h.PutCreature(h.P1, Creature(Civilization.Water, "Helper, pay rent"));
        h.P1.Deck.Clear();
        h.P1.Deck.AddRange(new[] { CardFactory.Creature(1, 1000, Civilization.Water, "DeckA") });
        var priorHand = h.P1.Hand.Count;

        var abilityIdx = h.P1.BattleZone.IndexOf(fortress);
        var payerIdx = h.P1.BattleZone.IndexOf(helper);
        Assert.True(h.Game.CanUseCrewAbility(abilityIdx, payerIdx));

        h.Game.ActivateCrewAbility(abilityIdx, payerIdx);

        // The payer spent its tap and the draw resolved; the crew holder never moved.
        Assert.True(helper.IsTapped);
        Assert.False(fortress.IsTapped);
        Assert.Equal(priorHand + 1, h.P1.Hand.Count);
    }

    [Fact]
    public void Crew_TheCrewHolderItself_MayAlwaysPay()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var fortress = h.PutCreature(h.P1, CrewCard("Fortress", "Water",
            CardFactory.Eff(EffectId.Tap_Draw, value: 1)));
        h.P1.Deck.Clear();
        h.P1.Deck.Add(CardFactory.Creature(1, 1000, Civilization.Water, "DeckA"));

        var idx = h.P1.BattleZone.IndexOf(fortress);
        Assert.True(h.Game.CanUseCrewAbility(idx, idx));
        h.Game.ActivateCrewAbility(idx, idx);

        Assert.True(fortress.IsTapped);
        Assert.Single(h.P1.Hand);
    }

    [Fact]
    public void Crew_AWrongCivilizationPayer_IsRejected()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var fortress = h.PutCreature(h.P1, CrewCard("Fortress", "Water",
            CardFactory.Eff(EffectId.Tap_Draw, value: 1)));
        var fire = h.PutCreature(h.P1, Creature(Civilization.Fire, "Burning hot"));

        var ex = Assert.Throws<RuleViolationException>(() =>
            h.Game.ActivateCrewAbility(h.P1.BattleZone.IndexOf(fortress), h.P1.BattleZone.IndexOf(fire)));
        Assert.Contains("not a Water creature", ex.Message);
        Assert.False(fire.IsTapped);
        Assert.False(fortress.IsTapped);
        Assert.Empty(h.P1.Hand);
        Assert.False(h.Game.CanUseCrewAbility(h.P1.BattleZone.IndexOf(fortress), h.P1.BattleZone.IndexOf(fire)));
    }

    [Fact]
    public void Crew_ATappedPayer_IsRejectedAndTheTapIsNotPaid()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var fortress = h.PutCreature(h.P1, CrewCard("Fortress", "Water",
            CardFactory.Eff(EffectId.Tap_Draw, value: 1)));
        var helper = h.PutCreature(h.P1, Creature(Civilization.Water, "Loafing"), tapped: true);

        var ex = Assert.Throws<RuleViolationException>(() =>
            h.Game.ActivateCrewAbility(h.P1.BattleZone.IndexOf(fortress), h.P1.BattleZone.IndexOf(helper)));
        Assert.Contains("already tapped", ex.Message);
        Assert.Empty(h.P1.Hand);
    }

    [Fact]
    public void Crew_ASummoningSickPayer_IsRejected()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var fortress = h.PutCreature(h.P1, CrewCard("Fortress", "Water",
            CardFactory.Eff(EffectId.Tap_Draw, value: 1)));
        var helper = h.PutCreature(h.P1, Creature(Civilization.Water, "Fresh off the field"), sick: true);

        Assert.False(h.Game.CanUseCrewAbility(h.P1.BattleZone.IndexOf(fortress), h.P1.BattleZone.IndexOf(helper)));
        var ex = Assert.Throws<RuleViolationException>(() =>
            h.Game.ActivateCrewAbility(h.P1.BattleZone.IndexOf(fortress), h.P1.BattleZone.IndexOf(helper)));
        Assert.Contains("summoning sickness", ex.Message);
        Assert.False(helper.IsTapped);
    }

    [Fact]
    public void Crew_OnACardWithoutACrewClause_IsRejected()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var mage = h.PutCreature(h.P1, CardFactory.TapCreature(3, 3000, Civilization.Water, "Mage",
            CardFactory.Eff(EffectId.Tap_Draw, value: 1)));
        var helper = h.PutCreature(h.P1, Creature(Civilization.Water, "Helper"));

        var ex = Assert.Throws<RuleViolationException>(() =>
            h.Game.ActivateCrewAbility(h.P1.BattleZone.IndexOf(mage), h.P1.BattleZone.IndexOf(helper)));
        Assert.Contains("no Crew ability", ex.Message);
        Assert.False(helper.IsTapped);
    }

    [Fact]
    public void Crew_ATappedHolder_CanStillBeUsedViaAnotherPayer()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var fortress = h.PutCreature(h.P1, CrewCard("Fortress", "Water",
            CardFactory.Eff(EffectId.Tap_Draw, value: 1)), tapped: true);
        var helper = h.PutCreature(h.P1, Creature(Civilization.Water, "Helper"));
        h.P1.Deck.Clear();
        h.P1.Deck.Add(CardFactory.Creature(1, 1000, Civilization.Water, "DeckA"));

        Assert.True(h.Game.CanUseCrewAbility(h.P1.BattleZone.IndexOf(fortress), h.P1.BattleZone.IndexOf(helper)));
        h.Game.ActivateCrewAbility(h.P1.BattleZone.IndexOf(fortress), h.P1.BattleZone.IndexOf(helper));

        Assert.True(helper.IsTapped);
        Assert.True(fortress.IsTapped); // stays as it was
        Assert.Single(h.P1.Hand);
    }

    // ------------------------------------------------------------- timing rules

    [Fact]
    public void Crew_IsGatedByTheAttackLock()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var fortress = h.PutCreature(h.P1, CrewCard("Fortress", "Water",
            CardFactory.Eff(EffectId.Tap_Draw, value: 1)));
        var helper = h.PutCreature(h.P1, Creature(Civilization.Water, "Helper"));
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Fire, "Charger"));
        h.SetShields(h.P2, CardFactory.Creature(1, 1000, Civilization.Light, "S1"), CardFactory.Creature(1, 1000, Civilization.Light, "S2"));

        h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(attacker));

        Assert.False(h.Game.CanUseCrewAbility(h.P1.BattleZone.IndexOf(fortress), h.P1.BattleZone.IndexOf(helper)));
        var ex = Assert.Throws<RuleViolationException>(() =>
            h.Game.ActivateCrewAbility(h.P1.BattleZone.IndexOf(fortress), h.P1.BattleZone.IndexOf(helper)));
        Assert.Contains("after a creature has attacked", ex.Message);
        Assert.False(helper.IsTapped);
    }

    [Fact]
    public void Crew_IsGatedWhileAShieldTriggerWindowIsOpen()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var fortress = h.PutCreature(h.P1, CrewCard("Fortress", "Water",
            CardFactory.Eff(EffectId.Tap_Draw, value: 1)));
        var helper = h.PutCreature(h.P1, Creature(Civilization.Water, "Helper"));
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Fire, "Charger"));
        var trigger = CardFactory.Creature(1, 1000, Civilization.Light, "Boom", Keyword.ShieldTrigger);
        h.SetShields(h.P2, trigger);

        h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(attacker));

        Assert.True(h.Game.ShieldTriggerWindowActive);
        Assert.False(h.Game.CanUseCrewAbility(h.P1.BattleZone.IndexOf(fortress), h.P1.BattleZone.IndexOf(helper)));
        var ex = Assert.Throws<RuleViolationException>(() =>
            h.Game.ActivateCrewAbility(h.P1.BattleZone.IndexOf(fortress), h.P1.BattleZone.IndexOf(helper)));
        Assert.Contains("Shield Trigger", ex.Message);
    }

    // -------------------------------------------------------- targeted crew use

    [Fact]
    public void Crew_TargetedAbility_TapsTheFoesCreature()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        // Arc Bine, Seeker of Explosions: light crew, tap one of the opponent's
        // creatures.
        var arcBine = h.PutCreature(h.P1, CrewCard("Arc Bine", "Light",
            CardFactory.Eff(EffectId.Tap_TapOpponentCreature, EffectTargetScope.OpponentCreature),
            Civilization.Light));
        var lightHelper = h.PutCreature(h.P1, Creature(Civilization.Light, "Angelic helper"));
        var foe = h.PutCreature(h.P2, Creature(Civilization.Fire, "Foe body"), tapped: false);

        h.Game.ActivateCrewAbility(
            h.P1.BattleZone.IndexOf(arcBine),
            h.P1.BattleZone.IndexOf(lightHelper),
            new[] { new SpellTarget(h.P2, h.P2.BattleZone.IndexOf(foe)) });

        Assert.True(lightHelper.IsTapped);
        Assert.False(arcBine.IsTapped);
        Assert.True(foe.IsTapped);
    }

    [Fact]
    public void Crew_TargetedAbility_NeedsALegalTargetBeforeItCanFire()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var arcBine = h.PutCreature(h.P1, CrewCard("Arc Bine", "Light",
            CardFactory.Eff(EffectId.Tap_TapOpponentCreature, EffectTargetScope.OpponentCreature),
            Civilization.Light));
        var lightHelper = h.PutCreature(h.P1, Creature(Civilization.Light, "Angelic helper"));

        Assert.False(h.Game.CanUseCrewAbility(h.P1.BattleZone.IndexOf(arcBine), h.P1.BattleZone.IndexOf(lightHelper)));
    }

    // ------------------------------------------------------------ AI behaviour

    [Fact]
    public void Ai_ChargesManaWithACrewAbility_UsingTheClanPayer()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var city = h.PutCreature(h.P1, CrewCard("Living City", "Nature",
            CardFactory.Eff(EffectId.Tap_ChargeMana), Civilization.Nature));
        // A nature creature to pay with; the AI pays with a non-holder clan creature
        // when it can, keeping the holder ready to attack.
        var helper = h.PutCreature(h.P1, Creature(Civilization.Nature, "Sylvan helper"));
        h.P1.Deck.Clear();
        h.P1.Deck.AddRange(new[] {
            CardFactory.Spell(3, Civilization.Nature, "Charge me"),
            CardFactory.Creature(1, 1000, Civilization.Nature, "Filler"),
        });

        var turn = new AiController(h.P1);
        // One Step runs the whole cascade: no mana charge / play possible (empty
        // hand), so it fires the Crew ability with the clan payer.
        turn.Step(h.Game);

        Assert.True(helper.IsTapped);
        Assert.False(city.IsTapped);
        Assert.Single(h.P1.ManaZone);
    }
}