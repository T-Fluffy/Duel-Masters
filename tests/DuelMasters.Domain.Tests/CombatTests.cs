using System.Linq;
using DuelMasters.Domain;
using Xunit;

namespace DuelMasters.Domain.Tests;

public class CombatTests
{
    [Fact]
    public void AttackCreature_HigherPowerWins_LoserToGraveyard_AttackerTaps()
    {
        var h = GameHarness.AtMainPhase();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 5000, Civilization.Fire, "Hero"));
        var target = h.PutCreature(h.P2, CardFactory.Creature(1, 3000, Civilization.Fire, "Victim"), tapped: true);

        h.Game.AttackCreature(0, 0);

        Assert.True(attacker.IsTapped);
        Assert.DoesNotContain(target, h.P2.BattleZone);
        Assert.Contains(target, h.P2.Graveyard);
        Assert.Contains(attacker, h.P1.BattleZone);
    }

    [Fact]
    public void AttackCreature_LowerPower_LosesAttackerToGraveyard()
    {
        var h = GameHarness.AtMainPhase();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 2000, Civilization.Fire, "Weak"));
        var target = h.PutCreature(h.P2, CardFactory.Creature(1, 6000, Civilization.Fire, "Strong"), tapped: true);

        h.Game.AttackCreature(0, 0);

        Assert.DoesNotContain(attacker, h.P1.BattleZone);
        Assert.Contains(attacker, h.P1.Graveyard);
        Assert.Contains(target, h.P2.BattleZone);
    }

    [Fact]
    public void AttackCreature_EqualPower_DestroysBoth()
    {
        var h = GameHarness.AtMainPhase();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 4000, Civilization.Fire, "A"));
        var target = h.PutCreature(h.P2, CardFactory.Creature(1, 4000, Civilization.Fire, "D"), tapped: true);

        h.Game.AttackCreature(0, 0);

        Assert.Empty(h.P1.BattleZone);
        Assert.Empty(h.P2.BattleZone);
        Assert.Contains(attacker, h.P1.Graveyard);
        Assert.Contains(target, h.P2.Graveyard);
    }

    [Fact]
    public void AttackCreature_RequiresTappedTarget()
    {
        var h = GameHarness.AtMainPhase();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 5000, Civilization.Fire, "Hero"));
        var target = h.PutCreature(h.P2, CardFactory.Creature(1, 3000, Civilization.Fire, "Standing"), tapped: false);

        Assert.Throws<RuleViolationException>(() => h.Game.AttackCreature(0, 0));
    }

    [Fact]
    public void AttackPlayer_Unblocked_BreaksShield_ToHand()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 5000, Civilization.Fire, "Hero"));

        var shieldCard = CardFactory.Creature(0, 1000, Civilization.Fire, "Shield");
        h.SetShields(h.P2, shieldCard);

        h.Game.AttackPlayer(0);

        Assert.Empty(h.P2.Shields);
        Assert.Single(h.P2.Hand); // broken shield went to hand
        Assert.Equal(shieldCard.Id, h.P2.Hand[0].Card.Id);
        Assert.Empty(h.P2.Graveyard);
        Assert.True(attacker.IsTapped);
    }

    [Fact]
    public void AttackPlayer_WithNoShields_Wins()
    {
        var h = GameHarness.AtMainPhase();
        h.PutCreature(h.P1, CardFactory.Creature(1, 5000, Civilization.Fire, "Hero"));
        h.SetShields(h.P2); // no shields

        h.Game.AttackPlayer(0);

        Assert.True(h.Game.IsGameOver);
        Assert.Same(h.P1, h.Game.Winner);
    }

    [Fact]
    public void Blocker_Intercepts_AndBattlesInsteadOfBreakingShields()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 5000, Civilization.Fire, "Hero"));
        var blocker = h.PutCreature(h.P2, CardFactory.Creature(1, 7000, Civilization.Fire, "Guard", Keyword.Blocker));
        h.SetShields(h.P2, CardFactory.Creature(0, 1000, Civilization.Fire, "Shield"));

        h.Game.AttackPlayer(0, h.P2, h.P2.BattleZone.IndexOf(blocker));

        // Battle happened: attacker (lower power) dies, blocker survives, no shield broken.
        Assert.DoesNotContain(attacker, h.P1.BattleZone);
        Assert.Contains(attacker, h.P1.Graveyard);
        Assert.Contains(blocker, h.P2.BattleZone);
        Assert.True(blocker.IsTapped);
        Assert.Equal(1, h.P2.ShieldCount);
        Assert.Empty(h.P2.Hand);
    }

    [Fact]
    public void Blocker_WithoutKeyword_CannotBlock()
    {
        var h = GameHarness.AtMainPhase();
        h.PutCreature(h.P1, CardFactory.Creature(1, 5000, Civilization.Fire, "Hero"));
        var noBlocker = h.PutCreature(h.P2, CardFactory.Creature(1, 7000, Civilization.Fire, "NotABlocker"));

        Assert.Throws<RuleViolationException>(
            () => h.Game.AttackPlayer(0, h.P2, h.P2.BattleZone.IndexOf(noBlocker)));
    }

    [Fact]
    public void Blocker_ThatIsTapped_CannotBlock()
    {
        var h = GameHarness.AtMainPhase();
        h.PutCreature(h.P1, CardFactory.Creature(1, 5000, Civilization.Fire, "Hero"));
        var tappedBlocker = h.PutCreature(h.P2, CardFactory.Creature(1, 7000, Civilization.Fire, "Tired", Keyword.Blocker), tapped: true);

        Assert.Throws<RuleViolationException>(
            () => h.Game.AttackPlayer(0, h.P2, h.P2.BattleZone.IndexOf(tappedBlocker)));
    }

    [Fact]
    public void SummoningSick_Blocker_CanStillBlock()
    {
        var h = GameHarness.AtMainPhase();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 3000, Civilization.Fire, "Hero"));
        var sickBlocker = h.PutCreature(h.P2, CardFactory.Creature(1, 5000, Civilization.Fire, "FreshGuard", Keyword.Blocker), sick: true);
        h.SetShields(h.P2, CardFactory.Creature(0, 1000, Civilization.Fire, "Shield"));

        // Summoning sickness only stops attacking, not blocking.
        h.Game.AttackPlayer(0, h.P2, h.P2.BattleZone.IndexOf(sickBlocker));

        Assert.Contains(sickBlocker, h.P2.BattleZone);
        Assert.True(sickBlocker.IsTapped);
        Assert.DoesNotContain(attacker, h.P1.BattleZone);
        Assert.Equal(1, h.P2.ShieldCount);
    }

    [Fact]
    public void OnlyDefender_CanBlock_AttackerCannotOfferOwnBlocker()
    {
        var h = GameHarness.AtMainPhase();
        h.PutCreature(h.P1, CardFactory.Creature(1, 5000, Civilization.Fire, "Hero"));
        var ownBlocker = h.PutCreature(h.P1, CardFactory.Creature(1, 7000, Civilization.Fire, "OwnGuard", Keyword.Blocker));

        // Attacker tries to block with its own creature - not allowed.
        Assert.Throws<RuleViolationException>(
            () => h.Game.AttackPlayer(0, h.P1, h.P1.BattleZone.IndexOf(ownBlocker)));
    }
}
