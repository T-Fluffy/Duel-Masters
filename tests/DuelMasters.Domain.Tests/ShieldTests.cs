using System.Linq;
using DuelMasters.Domain;
using Xunit;

namespace DuelMasters.Domain.Tests;

public class ShieldTests
{
    [Fact]
    public void BrokenShield_WithoutTrigger_GoesToHandNotGraveyard()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutCreature(h.P1, CardFactory.Creature(1, 5000, Civilization.Fire, "Hero"));
        var plain = CardFactory.Creature(0, 1000, Civilization.Fire, "Plain");
        h.SetShields(h.P2, plain);

        h.Game.AttackPlayer(0);

        Assert.Empty(h.P2.Shields);
        Assert.Empty(h.P2.Graveyard);
        var inHand = h.P2.Hand.FirstOrDefault(c => c.Card.Id == plain.Id);
        Assert.NotNull(inHand);
    }

    [Fact]
    public void BrokenShield_WithShieldTrigger_AlsoGoesToHand()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutCreature(h.P1, CardFactory.Creature(1, 5000, Civilization.Fire, "Hero"));
        var trigger = CardFactory.Creature(0, 1000, Civilization.Fire, "Trigger", Keyword.ShieldTrigger);
        h.SetShields(h.P2, trigger);

        h.Game.AttackPlayer(0);

        Assert.Empty(h.P2.Shields);
        // This milestone adds every broken shield to hand; the free-use interrupt
        // window is a later phase. Key assertion: it must NOT go to the graveyard.
        Assert.Empty(h.P2.Graveyard);
        Assert.Single(h.P2.Hand);
        Assert.Equal(trigger.Id, h.P2.Hand[0].Card.Id);
    }

    [Fact]
    public void DoubleBreaker_BreaksTwoShields()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 5000, Civilization.Fire, "Hero", Keyword.DoubleBreaker));
        h.SetShields(
            h.P2,
            CardFactory.Creature(0, 1000, Civilization.Fire, "S1"),
            CardFactory.Creature(0, 1000, Civilization.Fire, "S2"));

        h.Game.AttackPlayer(0);

        Assert.Empty(h.P2.Shields);
        Assert.Equal(2, h.P2.Hand.Count);
        Assert.True(attacker.IsTapped);
    }

    [Fact]
    public void BreakingAllShields_DoesNotImmediatelyWin_UntilUnblockedAttackWithZeroShields()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 5000, Civilization.Fire, "Hero", Keyword.DoubleBreaker));
        // Only 2 shields: one double-break attack takes them all to zero.
        h.SetShields(
            h.P2,
            CardFactory.Creature(0, 1000, Civilization.Fire, "S1"),
            CardFactory.Creature(0, 1000, Civilization.Fire, "S2"));

        h.Game.AttackPlayer(0);
        Assert.Equal(0, h.P2.ShieldCount);
        Assert.False(h.Game.IsGameOver); // reaching 0 alone does not end the game

        // A follow-up direct attack with zero shields seals the win.
        var second = h.PutCreature(h.P1, CardFactory.Creature(1, 5000, Civilization.Fire, "Second"));
        h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(second));
        Assert.True(h.Game.IsGameOver);
        Assert.Same(h.P1, h.Game.Winner);
    }
}
