using System.Linq;
using DuelMasters.Domain;
using Xunit;

namespace DuelMasters.Domain.Tests;

public class TurnFlowTests
{
    [Fact]
    public void StartGame_WithoutShuffle_PreservesDeckOrder_AndDealsShieldsAndHand()
    {
        var c = Enumerable.Range(0, 13).Select(i => CardFactory.Creature(1, 1000, Civilization.Fire, $"C{i}")).ToList();
        var p1 = CardFactory.PlayerWithDeck("P1", 13, c.ToArray());
        var p2 = CardFactory.PlayerWithDeck("P2", 13);
        var game = new DuelGame(p1, p2);

        game.StartGame(false);

        // Deck indices 0-4 -> shields, 5-9 -> opening hand, 10+ -> deck (top = 10).
        Assert.Equal(5, p1.Shields.Count);
        Assert.Equal(5, p1.Hand.Count);
        Assert.Equal(3, p1.Deck.Count);

        Assert.Equal(c[0], p1.Shields[0]);
        Assert.Equal(c[4], p1.Shields[4]);
        Assert.Equal(c[5].Id, p1.Hand[0].Card.Id);
        Assert.Equal(c[9].Id, p1.Hand[4].Card.Id);
        Assert.Equal(c[10], p1.Deck[0]); // deck top
        Assert.Equal(c[12], p1.Deck[2]);
    }

    [Fact]
    public void StartGame_WithLessThanTenCards_Throws()
    {
        var p1 = CardFactory.PlayerWithDeck("P1", 9);
        var p2 = CardFactory.PlayerWithDeck("P2", 40);
        var game = new DuelGame(p1, p2);

        Assert.Throws<RuleViolationException>(() => game.StartGame(false));
    }

    [Fact]
    public void StartTurn_ThenDraw_AddsTopCardAndEntersMainPhase()
    {
        var p1 = CardFactory.PlayerWithDeck("P1", 40);
        var p2 = CardFactory.PlayerWithDeck("P2", 40);
        var game = new DuelGame(p1, p2);
        game.StartGame(false);

        var top = p1.Deck[0];
        game.StartTurn();
        Assert.Equal(GamePhase.Draw, game.Phase);

        game.Draw();
        Assert.Equal(GamePhase.Main, game.Phase);
        Assert.Equal(top.Id, p1.Hand[^1].Card.Id);
    }

    [Fact]
    public void EndTurn_AdvancesToOpponent_AndIncrementsTurnNumber()
    {
        var p1 = CardFactory.PlayerWithDeck("P1", 40);
        var p2 = CardFactory.PlayerWithDeck("P2", 40);
        var game = new DuelGame(p1, p2);
        game.StartGame(false);

        game.StartTurn();
        game.Draw();
        game.EndMainPhase();
        game.EndTurn();

        Assert.Same(p2, game.ActivePlayer);
        Assert.Equal(2, game.TurnNumber);
        Assert.Equal(GamePhase.Untap, game.Phase);
    }

    [Fact]
    public void ManaCharge_IsLimitedToOnePerTurn()
    {
        var h = GameHarness.AtMainPhase();

        h.Game.PlayManaToManaZone(0);
        Assert.Single(h.P1.ManaZone);
        Assert.True(h.P1.ManaZone[0].IsTapped);

        Assert.Throws<RuleViolationException>(() => h.Game.PlayManaToManaZone(0));
    }

    [Fact]
    public void AfterAttack_SummonAndCastAreLocked()
    {
        var h = GameHarness.AtMainPhase();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 5000, Civilization.Fire, "Attacker"));

        h.Game.PlayManaToManaZone(0);
        h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(attacker));

        Assert.Throws<RuleViolationException>(() => h.Game.SummonCreature(0));
        Assert.Throws<RuleViolationException>(() => h.Game.CastSpell(0));
    }
}
