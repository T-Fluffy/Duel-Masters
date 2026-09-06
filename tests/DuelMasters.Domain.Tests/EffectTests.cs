using System.Linq;
using DuelMasters.Domain;
using Xunit;

namespace DuelMasters.Domain.Tests;

/// <summary>
/// Coverage for the card-effect engine: on-play / on-destroyed triggers, targeted
/// spells, combat keywords (Slayer, Power Attacker), and the Shield Trigger window.
/// </summary>
public class EffectTests
{
    private static void Mana(GameHarness h, Player owner, Civilization civ, int count = 1)
    {
        for (var i = 0; i < count; i++)
            h.PutMana(owner, CardFactory.Creature(i, 1000, civ, $"Mana-{i}"));
    }

    // ---------------------------------------------------------------- on play

    [Fact]
    public void OnPlay_Draw_PutsTopCardOfDeckIntoHand()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Fire);
        var cre = CardFactory.Creature(1, 2000, Civilization.Fire, "Cyclops",
            CardFactory.Eff(EffectId.OnPlay_Draw, value: 1));
        h.PutInHand(h.P1, cre);

        var before = h.P1.Deck.Count;
        var topId = h.P1.Deck[0].Id;
        h.Game.SummonCreature(0);

        Assert.Equal(before - 1, h.P1.Deck.Count);
        Assert.Equal(topId, h.P1.Hand[0].Card.Id);
    }

    // ------------------------------------------------------------ on destroyed

    [Fact]
    public void OnDestroyed_Draw_DrawsWhenCreatureDiesInBattle()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Fire, "Giant"));
        var victim = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Darkness, "Sacrifice",
            CardFactory.Eff(EffectId.OnDestroyed_Draw, value: 1)), tapped: true);

        var before = h.P2.Deck.Count;
        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(victim));

        Assert.Equal(before - 1, h.P2.Deck.Count);
        Assert.Single(h.P2.Hand);
    }

    // -------------------------------------------------------------- spell draw

    [Fact]
    public void DrawSpell_DrawsAndMovesToGraveyard_WithManaTapped()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Water, 3);
        var spell = CardFactory.Spell(3, Civilization.Water, "Tide",
            CardFactory.Eff(EffectId.Spell_Draw, value: 2));
        h.PutInHand(h.P1, spell);

        var before = h.P1.Deck.Count;
        var cast = h.Game.CastSpell(0);

        Assert.Equal(before - 2, h.P1.Deck.Count);
        Assert.Contains(cast, h.P1.Graveyard);
        Assert.Equal(3, h.P1.ManaZone.Count(m => m.IsTapped));
    }

    // ----------------------------------------------------------- destroy spell

    [Fact]
    public void DestroySpell_TargetsCreatureWithinThePowerLimit()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Darkness, 2);
        var spell = CardFactory.Spell(2, Civilization.Darkness, "Corpse",
            CardFactory.Eff(EffectId.Spell_DestroyPowerAtMost, EffectTargetScope.AnyCreature, value: 3000));
        h.PutInHand(h.P1, spell);
        var victim = h.PutCreature(h.P2, CardFactory.Creature(1, 2000, Civilization.Darkness, "Prey"));

        var cast = h.Game.CastSpell(0, h.P2, h.P2.BattleZone.IndexOf(victim));

        Assert.DoesNotContain(victim, h.P2.BattleZone);
        Assert.Contains(victim, h.P2.Graveyard);
        Assert.Contains(cast, h.P1.Graveyard);
    }

    [Fact]
    public void DestroySpell_RejectsCreatureAboveThePowerLimit_WithoutConsumingTheSpell()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Darkness, 2);
        var spell = CardFactory.Spell(2, Civilization.Darkness, "Corpse",
            CardFactory.Eff(EffectId.Spell_DestroyPowerAtMost, EffectTargetScope.AnyCreature, value: 3000));
        h.PutInHand(h.P1, spell);
        var tough = h.PutCreature(h.P2, CardFactory.Creature(1, 9000, Civilization.Darkness, "Tank"));

        Assert.Throws<RuleViolationException>(
            () => h.Game.CastSpell(0, h.P2, h.P2.BattleZone.IndexOf(tough)));

        Assert.Single(h.P1.Hand); // the spell stayed put
        Assert.All(h.P1.ManaZone, m => Assert.False(m.IsTapped)); // and mana was not spent
        Assert.Contains(tough, h.P2.BattleZone);
    }

    [Fact]
    public void DestroySpell_WithNoTarget_FizzlesHarmlessly()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Darkness, 2);
        var spell = CardFactory.Spell(2, Civilization.Darkness, "Corpse",
            CardFactory.Eff(EffectId.Spell_DestroyPowerAtMost, EffectTargetScope.AnyCreature, value: 3000));
        h.PutInHand(h.P1, spell);

        var cast = h.Game.CastSpell(0); // no target supplied

        Assert.Contains(cast, h.P1.Graveyard);
    }

    // -------------------------------------------------------- return to hand

    [Fact]
    public void ReturnToHandSpell_ReturnsTargetCreatureToItsOwnersHand()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Water, 4);
        var spell = CardFactory.Spell(4, Civilization.Water, "Whirlpool",
            CardFactory.Eff(EffectId.Spell_ReturnToHand, EffectTargetScope.AnyCreature));
        h.PutInHand(h.P1, spell);
        var target = h.PutCreature(h.P2, CardFactory.Creature(1, 5000, Civilization.Fire, "Crab"));

        h.Game.CastSpell(0, h.P2, h.P2.BattleZone.IndexOf(target));

        Assert.DoesNotContain(target, h.P2.BattleZone);
        Assert.False(target.IsTapped);
        Assert.Contains(target, h.P2.Hand);
    }

    // ---------------------------------------------------------------- tap spell

    [Fact]
    public void TapSpell_TapsTheTargetCreature()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Water, 3);
        var spell = CardFactory.Spell(3, Civilization.Water, "Freeze",
            CardFactory.Eff(EffectId.Spell_TapCreature, EffectTargetScope.OpponentCreature));
        h.PutInHand(h.P1, spell);
        var target = h.PutCreature(h.P2, CardFactory.Creature(1, 5000, Civilization.Fire, "Behemoth"));

        h.Game.CastSpell(0, h.P2, h.P2.BattleZone.IndexOf(target));

        Assert.True(target.IsTapped);
    }

    // --------------------------------------------------------------- untap spell

    private static (GameHarness h, CardInstance cre, Card spell) UntapScenario()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Light, 2);
        var cre = h.PutCreature(h.P1, CardFactory.Creature(1, 5000, Civilization.Light, "Paladin"), tapped: true);
        var spell = CardFactory.Spell(2, Civilization.Light, "Holy Awe",
            CardFactory.Eff(EffectId.Spell_UntapOwnCreature, EffectTargetScope.OwnCreature));
        h.PutInHand(h.P1, spell);
        return (h, cre, spell);
    }

    [Fact]
    public void UntapSpell_OnlyTargetsOwnCreatures()
    {
        var (h, _, _) = UntapScenario();
        var enemy = h.PutCreature(h.P2, CardFactory.Creature(1, 5000, Civilization.Fire, "Enemy"));

        Assert.Throws<RuleViolationException>(
            () => h.Game.CastSpell(0, h.P2, h.P2.BattleZone.IndexOf(enemy)));
    }

    [Fact]
    public void UntapSpell_UntapsTheTargetSoItCanAttack()
    {
        var (h, cre, _) = UntapScenario();
        h.SetShields(h.P2, CardFactory.Creature(0, 1000, Civilization.Fire, "Shield"));

        h.Game.CastSpell(0, h.P1, h.P1.BattleZone.IndexOf(cre));

        Assert.False(cre.IsTapped);
        h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(cre));
        Assert.True(cre.IsTapped); // the freed creature attacked
        Assert.Single(h.P2.Hand); // a shield was broken
    }

    // ---------------------------------------------------------------- boost spell

    [Fact]
    public void BoostSpell_TempPowerWinsABattleAndResetsNextTurn()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Nature, 2);
        var spell = CardFactory.Spell(2, Civilization.Nature, "Rage",
            CardFactory.Eff(EffectId.Spell_BoostPower, EffectTargetScope.OwnCreature, value: 2000));
        h.PutInHand(h.P1, spell);
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 2000, Civilization.Nature, "Brawler"));
        var target = h.PutCreature(h.P2, CardFactory.Creature(1, 3500, Civilization.Nature, "Sentinel"), tapped: true);

        h.Game.CastSpell(0, h.P1, h.P1.BattleZone.IndexOf(attacker));
        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(target));

        Assert.Contains(attacker, h.P1.BattleZone); // won thanks to +2000
        Assert.DoesNotContain(target, h.P2.BattleZone);

        // Back on P1's next turn the temporary boost is gone.
        h.Game.EndMainPhase();
        h.Game.EndTurn();
        h.Game.StartTurn();
        h.Game.Draw();
        h.Game.EndMainPhase();
        h.Game.EndTurn();
        h.Game.StartTurn();
        Assert.Equal(0, attacker.TempPower);
    }

    // ----------------------------------------------------------- power attacker

    [Fact]
    public void PowerAttacker_BoostsPowerWhileAttacking()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 2000, Civilization.Fire, "Smash",
            CardFactory.Eff(EffectId.PowerAttacker_AttackBoost, value: 2000)));
        var target = h.PutCreature(h.P2, CardFactory.Creature(1, 3500, Civilization.Fire, "Bulwark"), tapped: true);

        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(target));

        Assert.Contains(attacker, h.P1.BattleZone);
        Assert.DoesNotContain(target, h.P2.BattleZone);
    }

    // ------------------------------------------------------------------- slayer

    [Fact]
    public void Slayer_DefenderTakesAttackerDown_EvenWhenLosingThePowerBattle()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 6000, Civilization.Fire, "Behemoth"));
        var slayer = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "Deathslinger",
            Keyword.Slayer), tapped: true);

        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(slayer));

        Assert.Contains(slayer, h.P2.Graveyard);
        Assert.Contains(attacker, h.P1.Graveyard); // slain despite winning the power check
    }

    [Fact]
    public void Slayer_Blocker_BothGoToGraveAfterBlocking()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 5000, Civilization.Fire, "Raider"));
        var slayerBlocker = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "Assassin",
            Keyword.Slayer, Keyword.Blocker));
        h.SetShields(h.P2, CardFactory.Creature(0, 1000, Civilization.Fire, "Shield"));

        h.Game.AttackPlayer(0, h.P2, h.P2.BattleZone.IndexOf(slayerBlocker));

        Assert.Contains(slayerBlocker, h.P2.Graveyard);
        Assert.Contains(attacker, h.P1.Graveyard);
        Assert.Equal(1, h.P2.ShieldCount); // no shield was broken
    }

    // ----------------------------------------------------------- shield trigger

    [Fact]
    public void CreatureShieldTrigger_IsPlayableFreely_WithoutMana()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 5000, Civilization.Fire, "Raider"));
        var trigger = CardFactory.Creature(7, 3000, Civilization.Fire, "Dragon", Keyword.ShieldTrigger);
        h.SetShields(h.P2, trigger);

        h.Game.AttackPlayer(0);

        Assert.True(h.Game.ShieldTriggerWindowActive);
        Assert.Empty(h.P2.ManaZone); // the defender has zero mana
        h.Game.PlayShieldTrigger(0);

        Assert.False(h.Game.ShieldTriggerWindowActive);
        Assert.Contains(trigger.Id, h.P2.BattleZone.Select(x => x.Card.Id));
        Assert.Empty(h.P2.Hand); // moved straight from the trigger to the battle zone
    }

    [Fact]
    public void SpellShieldTrigger_ResolvesForFree_WithTarget()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 5000, Civilization.Fire, "Raider"));
        var triggerSpell = CardFactory.Spell(2, Civilization.Darkness, "Ambush",
            CardFactory.Eff(EffectId.Spell_DestroyPowerAtMost, EffectTargetScope.AnyCreature, value: 3000),
            Keyword.ShieldTrigger);
        h.SetShields(h.P2, triggerSpell);

        h.Game.AttackPlayer(0);

        // The defender has no mana, but the trigger must resolve anyway.
        var victim = h.PutCreature(h.P1, CardFactory.Creature(1, 2000, Civilization.Fire, "Scout"));
        h.Game.PlayShieldTrigger(0, h.P1, h.P1.BattleZone.IndexOf(victim));

        Assert.DoesNotContain(victim, h.P1.BattleZone);
        Assert.Contains(victim, h.P1.Graveyard);
        Assert.Empty(h.P2.ManaZone);
        Assert.False(h.Game.ShieldTriggerWindowActive);
    }

    [Fact]
    public void DeclineShieldTrigger_KeepsCardInHand_AndClosesWindow()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 5000, Civilization.Fire, "Raider"));
        var trigger = CardFactory.Creature(3, 3000, Civilization.Fire, "Dragon", Keyword.ShieldTrigger);
        h.SetShields(h.P2, trigger);

        h.Game.AttackPlayer(0);
        var instance = h.P2.Hand.Single();
        h.Game.DeclineShieldTriggers();

        Assert.False(h.Game.ShieldTriggerWindowActive);
        Assert.Same(instance, h.P2.Hand.Single());
        Assert.Empty(h.P2.BattleZone);
    }

    [Fact]
    public void OpenTriggerWindow_BlocksOtherActionsUntilResolved()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 5000, Civilization.Fire, "Raider"));
        var trigger = CardFactory.Creature(3, 3000, Civilization.Fire, "Dragon", Keyword.ShieldTrigger);
        h.SetShields(h.P2, trigger);
        h.Game.AttackPlayer(0);

        // A guarded action while the trigger window is open.
        h.PutInHand(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "Extra"));
        Assert.Throws<RuleViolationException>(() => h.Game.SummonCreature(0));

        h.Game.DeclineShieldTriggers();
        h.Game.PlayManaToManaZone(0); // no longer blocked
    }
}