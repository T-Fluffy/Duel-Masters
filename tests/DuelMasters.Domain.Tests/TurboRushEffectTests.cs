using System.Linq;
using DuelMasters.Domain;
using Xunit;

namespace DuelMasters.Domain.Tests;

/// <summary>
/// Coverage for the Phase D engine expansion: DM-08 continuous/triggered
/// abilities (always-on power boosts, the "Turbo Rush" speed-attacker aura that
/// clears summoning sickness, and attack / unblocked / blocked triggers).
/// </summary>
public class TurboRushEffectTests
{
    // ----------------------------------------------------------- static boost

    [Fact]
    public void StaticPower_AlwaysBoost_IsPermanentBattlePower()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var senia = h.PutCreature(h.P1, CardFactory.Creature(1, 2000, Civilization.Nature, "Senia",
            CardFactory.Eff(EffectId.StaticPower_AlwaysBoost, value: 5000)));
        var enemy = h.PutCreature(h.P2, CardFactory.Creature(1, 6000, Civilization.Fire, "Bulk"), tapped: true);

        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(senia), h.P2.BattleZone.IndexOf(enemy));

        Assert.Contains(senia, h.P1.BattleZone); // 2000 + 5000 beats 6000
        Assert.Contains(enemy, h.P2.Graveyard);
    }

    // ---------------------------------------------------- Turbo Rush aura / sick

    [Fact]
    public void TurboSpeedAttackerAura_SummoningClearsExistingAndOwnSickness()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Nature, 5);
        var a = h.PutCreature(h.P1, CardFactory.Creature(2, 1000, Civilization.Fire, "Comrade A"), sick: true);
        var b = h.PutCreature(h.P1, CardFactory.Creature(2, 1000, Civilization.Fire, "Comrade B"), sick: true);
        var enemy = h.PutCreature(h.P2, CardFactory.Creature(2, 1000, Civilization.Fire, "Enemy"), sick: true);
        h.PutInHand(h.P1, CardFactory.Creature(5, 6000, Civilization.Nature, "Jagalzor",
            CardFactory.Eff(EffectId.StaticTurbo_SpeedAttackerAll)));

        h.Game.SummonCreature(0);

        Assert.False(a.IsSummoningSick);
        Assert.False(b.IsSummoningSick);
        Assert.False(h.P1.BattleZone.Single(c => c.Card.Name == "Jagalzor").IsSummoningSick);
        Assert.True(enemy.IsSummoningSick); // only the controller's creatures cheer up
    }

    [Fact]
    public void TurboSpeedAttackerAura_FutureSummonsEnterUntapped()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutCreature(h.P1, CardFactory.Creature(5, 6000, Civilization.Nature, "Jagalzor",
            CardFactory.Eff(EffectId.StaticTurbo_SpeedAttackerAll)));
        Mana(h, h.P1, Civilization.Fire, 2);
        h.PutInHand(h.P1, CardFactory.Creature(2, 3000, Civilization.Fire, "Fresh Trooper"));

        h.Game.SummonCreature(0);

        Assert.False(h.P1.BattleZone.Single(c => c.Card.Name == "Fresh Trooper").IsSummoningSick);
    }

    [Fact]
    public void TurboSpeedAttackerAura_WithoutAura_CreatureStaysSummonSick()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Fire, 2);
        h.PutInHand(h.P1, CardFactory.Creature(2, 3000, Civilization.Fire, "Plain Trooper"));

        h.Game.SummonCreature(0);

        Assert.True(h.P1.BattleZone.Single().IsSummoningSick);
    }

    // ------------------------------------------------------------ attack triggers

    [Fact]
    public void AttackTrigger_OpponentDiscardsHand_OnPlayerAttack()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutCreature(h.P1, CardFactory.Creature(5, 6000, Civilization.Fire, "Gigaclaws",
            CardFactory.Eff(EffectId.AttackTrigger_OpponentDiscardsHand)));
        var discardMe1 = h.PutInHand(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "D1"));
        var discardMe2 = h.PutInHand(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "D2"));
        h.SetShields(h.P2, CardFactory.Creature(0, 1000, Civilization.Fire, "Shield"));

        h.Game.AttackPlayer(0);

        Assert.Single(h.P2.Hand); // hand discarded, then the broken shield went to hand
        Assert.Equal("Shield", h.P2.Hand[0].Card.Name);
        Assert.Contains(discardMe1, h.P2.Graveyard);
        Assert.Contains(discardMe2, h.P2.Graveyard);
        Assert.Empty(h.P2.Shields);
    }

    [Fact]
    public void AttackTrigger_OpponentDiscardsHand_OnCreatureAttack()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(5, 5000, Civilization.Fire, "Gigaclaws",
            CardFactory.Eff(EffectId.AttackTrigger_OpponentDiscardsHand)));
        var target = h.PutCreature(h.P2, CardFactory.Creature(1, 3000, Civilization.Fire, "Meat Shield"), tapped: true);
        var discardMe = h.PutInHand(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "D1"));

        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(target));

        Assert.Empty(h.P2.Hand);
        Assert.Contains(discardMe, h.P2.Graveyard);
        Assert.Contains(attacker, h.P1.BattleZone);
    }

    [Fact]
    public void AttackTrigger_UntapAllOwnExceptSelf_OnUnblockedAttack()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var solar = h.PutCreature(h.P1, CardFactory.Creature(4, 3000, Civilization.Nature, "Solar Grass",
            CardFactory.Eff(EffectId.AttackTrigger_UntapAllOwnExceptSelf)));
        var helper = h.PutCreature(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "Helper"), tapped: true);
        var enemy = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "Sleepy"), tapped: true);
        h.SetShields(h.P2, CardFactory.Creature(0, 1000, Civilization.Fire, "Shield"));

        h.Game.AttackPlayer(0);

        Assert.True(solar.IsTapped);       // the attacker attacked, so it stays tapped
        Assert.False(helper.IsTapped);     // every other own creature untapped
        Assert.True(enemy.IsTapped);       // the opponent's staying power is untouched
        Assert.Empty(h.P2.Shields);
        Assert.Contains(solar, h.P1.BattleZone);
    }

    // ------------------------------------------------------------ blocked trigger

    [Fact]
    public void BlockedTrigger_BreakOneShield_OnBlockedAttack()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var scarab = h.PutCreature(h.P1, CardFactory.Creature(5, 5000, Civilization.Fire, "Carbonite Scarab",
            CardFactory.Eff(EffectId.BlockedTrigger_BreakOneShield)));
        var blocker = h.PutCreature(h.P2, CardFactory.Creature(1, 3000, Civilization.Fire, "Guard", Keyword.Blocker), tapped: false);
        var shield = CardFactory.Creature(0, 1000, Civilization.Fire, "Shield");
        h.SetShields(h.P2, shield);

        h.Game.AttackPlayer(0, h.P2, h.P2.BattleZone.IndexOf(blocker));

        Assert.Contains(scarab, h.P1.BattleZone);   // battle won (5000 > 3000)
        Assert.Contains(blocker, h.P2.Graveyard);
        Assert.Empty(h.P2.Shields);                // the blocked attack broke a shield anyway
        Assert.Single(h.P2.Hand);
        Assert.Equal(shield.Id, h.P2.Hand[0].Card.Id);
    }

    private static void Mana(GameHarness h, Player owner, Civilization civ, int count = 1)
    {
        for (var i = 0; i < count; i++)
            h.PutMana(owner, CardFactory.Creature(i, 1000, civ, $"Mana-{i}"));
    }
}