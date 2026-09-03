using System.Linq;
using DuelMasters.Domain;
using DuelMasters.Domain.Networking;
using Xunit;

namespace DuelMasters.Domain.Tests;

/// <summary>
/// Verifies the viewer-relative <see cref="DuelGameState"/> snapshot: the opponent's
/// hand is redacted to face-down entries while the viewer's own hand is revealed,
/// and the active side / winner are mapped to the fixed Player1/Player2 sides.
/// </summary>
public class NetworkingStateTests
{
    [Fact]
    public void State_HidesOpponentHand_ButRevealsOwn()
    {
        var h = GameHarness.AtMainPhase();

        var state = DuelGameState.From(h.Game, "ABCDEF", DuelSide.Player1);

        var me = state.Players.Single(p => p.Side == DuelSide.Player1);
        var they = state.Players.Single(p => p.Side == DuelSide.Player2);

        Assert.Equal(6, me.Hand.Count);
        Assert.True(me.Hand.All(c => !c.CountOnly));
        Assert.Equal("ABCDEF", state.MatchCode);

        Assert.Equal(5, they.Hand.Count);
        Assert.True(they.Hand.All(c => c.CountOnly));
    }

    [Fact]
    public void State_RevealsManaAndBattle_ForBothSides()
    {
        var h = GameHarness.AtMainPhase();
        var creature = CardFactory.Creature(cost: 2, power: 4000, name: "Swift");
        h.PutCreature(h.P2, creature, tapped: true, sick: false);
        h.PutMana(h.P1, CardFactory.Creature(cost: 1, power: 1000));

        var state = DuelGameState.From(h.Game, "AB12CD", DuelSide.Player2);

        var p2 = state.Players.Single(p => p.Side == DuelSide.Player2);
        var p1 = state.Players.Single(p => p.Side == DuelSide.Player1);

        var creatureState = p2.BattleZone.Single();
        Assert.Equal("Swift", creatureState.Name);
        Assert.True(creatureState.IsTapped);
        Assert.Equal(4000, creatureState.Power);

        Assert.Single(p1.ManaZone);
        Assert.False(p1.ManaZone[0].IsTapped);
    }

    [Fact]
    public void State_MapsActiveSide_AndWinner()
    {
        var h = GameHarness.AtMainPhase();

        var state = DuelGameState.From(h.Game, "CODE12", DuelSide.Player1);

        Assert.Equal(DuelSide.Player1, state.ActiveSide);
        Assert.True(state.YourTurn);
        Assert.True(state.CanPlayMana);
        Assert.False(state.IsGameOver);
        Assert.Null(state.WinnerId);
        Assert.Equal("Main", state.Phase);
    }
}
