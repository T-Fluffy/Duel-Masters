using DuelMasters.Domain;
using Xunit;

namespace DuelMasters.Domain.Tests;

public class ManaAndSummonTests
{
    [Fact]
    public void Summon_CostsMana_AndTapsSpentMana()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutMana(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "FireMana"));
        h.PutInHand(h.P1, CardFactory.Creature(1, 2000, Civilization.Fire, "FireBeast"));

        var instance = h.Game.SummonCreature(0);

        Assert.Single(h.P1.BattleZone);
        Assert.Same(instance, h.P1.BattleZone[0]);
        Assert.True(instance.IsSummoningSick);
        Assert.True(h.P1.ManaZone[0].IsTapped); // the paid mana was tapped
    }

    [Fact]
    public void Summon_RequiresAtLeastOneMatchingCivilization()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        // Only Nature mana is available, but the creature is Fire.
        h.PutMana(h.P1, CardFactory.Creature(1, 1000, Civilization.Nature, "NatureMana"));
        h.PutInHand(h.P1, CardFactory.Creature(1, 2000, Civilization.Fire, "FireBeast"));

        Assert.Throws<RuleViolationException>(() => h.Game.SummonCreature(0));
    }

    [Fact]
    public void Summon_CantAffordCost_Throws()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        // One Fire mana available, but the creature costs 5.
        h.PutMana(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "FireMana"));
        h.PutInHand(h.P1, CardFactory.Creature(5, 2000, Civilization.Fire, "Expensive"));

        Assert.Throws<RuleViolationException>(() => h.Game.SummonCreature(0));
    }

    [Fact]
    public void SummoningSick_CreatureCannotAttack_UntilItsOwnersNextTurn()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutMana(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "FireMana"));
        var creature = CardFactory.Creature(1, 2000, Civilization.Fire, "Soldier");
        h.PutInHand(h.P1, creature);

        var instance = h.Game.SummonCreature(0);
        Assert.True(instance.IsSummoningSick);
        Assert.Throws<RuleViolationException>(() => h.Game.AttackPlayer(0));

        // Advance through P2's full turn back to P1; then P1's start-of-turn untaps
        // and clears summoning sickness on P1's creatures.
        h.Game.EndMainPhase();
        h.Game.EndTurn();
        h.Game.StartTurn();
        h.Game.Draw();
        h.Game.EndMainPhase();
        h.Game.EndTurn();
        h.Game.StartTurn();
        Assert.False(instance.IsSummoningSick);
    }

    [Fact]
    public void SpeedAttacker_CanAttackTheTurnItIsSummoned()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutMana(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "FireMana"));
        h.SetShields(h.P2, CardFactory.Creature(0, 1000, Civilization.Fire, "Shield"));
        var speed = CardFactory.Creature(1, 2000, Civilization.Fire, "Fast", Keyword.SpeedAttacker);
        h.PutInHand(h.P1, speed);

        var instance = h.Game.SummonCreature(0);
        Assert.False(instance.IsSummoningSick);
        h.Game.AttackPlayer(0);
    }

    [Fact]
    public void CastSpell_PaysMana_RemovesFromHand_AndSendsToGraveyard()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutMana(h.P1, CardFactory.Creature(1, 1000, Civilization.Water, "WaterMana"));
        h.PutInHand(h.P1, CardFactory.Spell(1, Civilization.Water, "Waterspell"));

        var instance = h.Game.CastSpell(0);

        Assert.DoesNotContain(instance, h.P1.Hand);
        Assert.Single(h.P1.Graveyard);
        Assert.Same(instance, h.P1.Graveyard[0]);
    }

    [Fact]
    public void CastSpell_RequiresMatchingCivilization()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutMana(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "FireMana"));
        h.PutInHand(h.P1, CardFactory.Spell(1, Civilization.Water, "Waterspell"));

        Assert.Throws<RuleViolationException>(() => h.Game.CastSpell(0));
    }
}
