using System.Linq;
using DuelMasters.Domain;
using Xunit;

namespace DuelMasters.Domain.Tests;

/// <summary>
/// Coverage for the Phase 4 rule expansion: the new static combat keywords,
/// continuous / attack-time power effects, cost modifiers, the Survivor end-turn
/// rule, "attacks each turn" enforcement and spell-gated summoning.
/// </summary>
public class ExpansionRuleTests
{
    private static void Mana(GameHarness h, Player owner, Civilization civ, int count = 1)
    {
        for (var i = 0; i < count; i++)
            h.PutMana(owner, CardFactory.Creature(i, 1000, civ, $"Mana-{i}"));
    }

    private static (GameHarness h, CardInstance attacker, CardInstance target) Ready(
        Card attackerCard = null!,
        Card targetCard = null!)
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, attackerCard ?? CardFactory.Creature(1, 4000, Civilization.Fire, "Raider"));
        var target = h.PutCreature(h.P2, targetCard ?? CardFactory.Creature(1, 3000, Civilization.Fire, "Sentinel"), tapped: true);
        return (h, attacker, target);
    }

    // ---------------------------------------------------- static combat keywords

    [Fact]
    public void Unblockable_Attacker_CannotBeBlocked()
    {
        var (h, attacker, _) = Ready(attackerCard: CardFactory.Creature(1, 4000, Civilization.Fire, "Spirit",
            Keyword.Unblockable));
        var blocker = h.PutCreature(h.P2, CardFactory.Creature(1, 5000, Civilization.Fire, "Guard", Keyword.Blocker));
        h.SetShields(h.P2, CardFactory.Creature(0, 1000, Civilization.Fire, "Shield"));

        Assert.False(DuelGame.CanBeBlocked(attacker.Card));
        Assert.Throws<RuleViolationException>(
            () => h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(attacker), h.P2, h.P2.BattleZone.IndexOf(blocker)));
    }

    [Fact]
    public void CannotAttackPlayers_BlocksDirectAttacks()
    {
        var (h, attacker, _) = Ready(attackerCard: CardFactory.Creature(1, 4000, Civilization.Fire, "Loner",
            Keyword.CannotAttackPlayers));

        Assert.Throws<RuleViolationException>(
            () => h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(attacker)));
    }

    [Fact]
    public void CannotAttackCreatures_BlocksCreatureAttacks()
    {
        var (h, attacker, target) = Ready(attackerCard: CardFactory.Creature(1, 4000, Civilization.Fire, "Pacifist",
            Keyword.CannotAttackCreatures));

        Assert.Throws<RuleViolationException>(
            () => h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(target)));
    }

    [Fact]
    public void CanAttackUntappedCreatures_IgnoresTheTappedRule()
    {
        var (h, attacker, target) = Ready(attackerCard: CardFactory.Creature(1, 4000, Civilization.Fire, "Hunter",
            Keyword.CanAttackUntappedCreatures));
        target.IsTapped = false;

        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(target));

        Assert.True(attacker.IsTapped);
        Assert.Contains(target, h.P2.Graveyard);
    }

    [Fact]
    public void CannotBeAttacked_ProtectsTheCreature()
    {
        var (h, attacker, target) = Ready(targetCard: CardFactory.Creature(1, 3000, Civilization.Nature, "Fortress",
            Keyword.CannotBeAttacked));

        Assert.Throws<RuleViolationException>(
            () => h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(target)));
    }

    [Fact]
    public void CannotAttackOutnumbered_EndsWhenTheOpponentHasMoreCreatures()
    {
        var (h, attacker, _) = Ready(attackerCard: CardFactory.Creature(1, 4000, Civilization.Fire, "Coward",
            Keyword.CannotAttackOutnumbered));
        h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "Extra"), tapped: true);

        Assert.Throws<RuleViolationException>(
            () => h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(attacker)));
    }

    // ------------------------------------------------- attacks each turn

    [Fact]
    public void AttacksEachTurn_BlocksEndingTheMainPhaseUntilItAttacks()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var rager = h.PutCreature(h.P1, CardFactory.Creature(1, 4000, Civilization.Fire, "Rager",
            Keyword.AttacksEachTurn));
        h.SetShields(h.P2, CardFactory.Creature(0, 1000, Civilization.Fire, "Shield"));

        Assert.Contains(rager, h.Game.MustAttackList());
        Assert.Throws<RuleViolationException>(() => h.Game.EndMainPhase());

        h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(rager));

        Assert.DoesNotContain(rager, h.Game.MustAttackList());
        h.Game.EndMainPhase();
        Assert.Equal(GamePhase.End, h.Game.Phase);
    }

    [Fact]
    public void AttacksEachTurn_WithNoLegalAttack_DoesNotBlockTheTurn()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutCreature(h.P1, CardFactory.Creature(1, 4000, Civilization.Fire, "Rager",
            Keyword.AttacksEachTurn, Keyword.CannotAttackPlayers));
        // No adversary creatures to attack and no shields, so the Rager has no legal attack.

        Assert.Empty(h.Game.MustAttackList());
        h.Game.EndMainPhase();
        Assert.Equal(GamePhase.End, h.Game.Phase);
    }

    // -------------------------------------------------------------- survivor

    [Fact]
    public void Survivor_AloneAtTurnEnd_IsDestroyed()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var survivor = h.PutCreature(h.P2, CardFactory.Creature(1, 2000, Civilization.Water, "Loner",
            Keyword.Survivor));

        h.PutInHand(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "Filler"));
        h.Game.EndMainPhase();
        h.Game.EndTurn();

        Assert.Contains(survivor, h.P2.Graveyard);
    }

    [Fact]
    public void Survivor_WithCompany_SurvivesTheTurn()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var survivor = h.PutCreature(h.P2, CardFactory.Creature(1, 2000, Civilization.Water, "Loner",
            Keyword.Survivor));
        h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Water, "Friend"));

        h.PutInHand(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "Filler"));
        h.Game.EndMainPhase();
        h.Game.EndTurn();

        Assert.Contains(survivor, h.P2.BattleZone);
    }

    // ------------------------------------------------- static / attack power

    [Fact]
    public void StaticPower_AuraRace_BuffsOtherMatchingCreatures()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutCreature(h.P1, CardFactory.Creature(1, 1000, Civilization.Light, "Dragon Aura",
            CardFactory.Eff(EffectId.StaticPower_AuraRace, value: 2000, data: "Dragon")));
        var dragon = h.PutCreature(h.P1, CardFactory.CreatureWithRace(1, 2500, Civilization.Fire, "Dragon Knight", "Dragon"));
        var enemy = h.PutCreature(h.P2, CardFactory.Creature(1, 3500, Civilization.Fire, "Bulk"), tapped: true);

        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(dragon), h.P2.BattleZone.IndexOf(enemy));

        Assert.Contains(dragon, h.P1.BattleZone); // 2500 + 2000 beat the 3500 bulk
        Assert.Contains(enemy, h.P2.Graveyard);
    }

    [Fact]
    public void StaticPower_AlwaysWhileHaveRace_AppliesOnlyWhileSupported()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var beast = h.PutCreature(h.P1, CardFactory.Creature(1, 1500, Civilization.Nature, "Alpha Beast",
            CardFactory.Eff(EffectId.StaticPower_AlwaysWhileHaveRace, value: 2000, data: "Beast")));
        var enemy = h.PutCreature(h.P2, CardFactory.Creature(1, 3000, Civilization.Fire, "Bulk"), tapped: true);

        // Without a Beast ally, 1500 loses the 3000 battle.
        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(beast), h.P2.BattleZone.IndexOf(enemy));
        Assert.Contains(beast, h.P1.Graveyard);

        // With a Beast ally the boost kicks in: fresh 1500 Beast + supporter beats 3000.
        var h2 = GameHarness.AtMainPhase();
        h2.ResetBoard();
        var beast2 = h2.PutCreature(h2.P1, CardFactory.Creature(1, 1500, Civilization.Nature, "Alpha Beast 2",
            CardFactory.Eff(EffectId.StaticPower_AlwaysWhileHaveRace, value: 2000, data: "Beast")));
        h2.PutCreature(h2.P1, CardFactory.CreatureWithRace(1, 1000, Civilization.Nature, "Militia", "Beast"));
        var enemy2 = h2.PutCreature(h2.P2, CardFactory.Creature(1, 3000, Civilization.Fire, "Bulk 2"), tapped: true);

        h2.Game.AttackCreature(h2.P1.BattleZone.IndexOf(beast2), h2.P2.BattleZone.IndexOf(enemy2));
        Assert.Contains(beast2, h2.P1.BattleZone);
        Assert.Contains(enemy2, h2.P2.Graveyard);
    }

    [Fact]
    public void StaticPower_AlwaysPerOtherCreature_BuffsPerOwnCreature()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var lead = h.PutCreature(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "Pack Leader",
            CardFactory.Eff(EffectId.StaticPower_AlwaysPerOtherCreature, value: 1000)));
        h.PutCreature(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "Follower 1"));
        h.PutCreature(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "Follower 2"));
        var enemy = h.PutCreature(h.P2, CardFactory.Creature(1, 2500, Civilization.Fire, "Bulk"), tapped: true);

        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(lead), h.P2.BattleZone.IndexOf(enemy));

        Assert.Contains(lead, h.P1.BattleZone); // 1000 + 2*1000 beats 2500
        Assert.Contains(enemy, h.P2.Graveyard);
    }

    [Fact]
    public void StaticPower_AttackPerGraveyardCiv_ScalesWithGraveCount()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 2000, Civilization.Fire, "Grave Wraith",
            CardFactory.Eff(EffectId.StaticPower_AttackPerGraveyardCiv, value: 1000, data: "Fire")));
        h.P1.Graveyard.Add(new CardInstance(CardFactory.Creature(1, 1000, Civilization.Fire, "Ash 1"), h.P1) { Zone = Zone.Graveyard });
        h.P1.Graveyard.Add(new CardInstance(CardFactory.Creature(1, 1000, Civilization.Fire, "Ash 2"), h.P1) { Zone = Zone.Graveyard });
        var enemy = h.PutCreature(h.P2, CardFactory.Creature(1, 3500, Civilization.Fire, "Bulk"), tapped: true);

        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(enemy));

        Assert.Contains(attacker, h.P1.BattleZone); // 2000 + 2*1000 beats 3500
        Assert.Contains(enemy, h.P2.Graveyard);
    }

    [Fact]
    public void StaticPower_AttackPerOtherCreature_AddsAttackTimePower()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 1500, Civilization.Fire, "War Chant",
            CardFactory.Eff(EffectId.StaticPower_AttackPerOtherCreature, value: 1000)));
        h.PutCreature(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "Mate 1"));
        h.PutCreature(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "Mate 2"));
        var enemy = h.PutCreature(h.P2, CardFactory.Creature(1, 3000, Civilization.Fire, "Bulk"), tapped: true);

        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(enemy));

        Assert.Contains(attacker, h.P1.BattleZone); // 1500 + 2*1000 beats 3000
        Assert.Contains(enemy, h.P2.Graveyard);
    }

    [Fact]
    public void StaticPower_AttackWhileHaveRace_AddsPowerWithARaceAlly()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 1500, Civilization.Fire, "Beast Master",
            CardFactory.Eff(EffectId.StaticPower_AttackWhileHaveRace, value: 2000, data: "Beast")));
        h.PutCreature(h.P1, CardFactory.CreatureWithRace(1, 1000, Civilization.Nature, "Own Beast", "Beast"));
        var enemy = h.PutCreature(h.P2, CardFactory.Creature(1, 3000, Civilization.Fire, "Bulk"), tapped: true);

        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(enemy));

        Assert.Contains(attacker, h.P1.BattleZone); // 1500 + 2000 beats 3000
        Assert.Contains(enemy, h.P2.Graveyard);
    }

    // ------------------------------------------------------------ cost mods

    [Fact]
    public void CostDecrease_Summon_All_LowersSummoningCost()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutCreature(h.P1, CardFactory.Creature(1, 1000, Civilization.Nature, "Nature Warlord",
            CardFactory.Eff(EffectId.CostDecrease_Summon_All, value: 1)));
        Mana(h, h.P1, Civilization.Nature, 2);
        var cre = CardFactory.Creature(3, 3000, Civilization.Nature, "Expensive");
        h.PutInHand(h.P1, cre);

        Assert.Equal(2, h.Game.CardManaCost(h.P1, cre));
        h.Game.SummonCreature(0);

        Assert.Contains(cre.Id, h.P1.BattleZone.Select(x => x.Card.Id));
    }

    [Fact]
    public void CostIncrease_Summon_ByCiv_AppliesGlobally()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Light, "Fire Tax",
            CardFactory.Eff(EffectId.CostIncrease_Summon_ByCiv, value: 2, data: "Fire")));
        Mana(h, h.P1, Civilization.Fire, 3);
        var cre = CardFactory.Creature(2, 3000, Civilization.Fire, "Criminal");
        h.PutInHand(h.P1, cre);

        Assert.Equal(4, h.Game.CardManaCost(h.P1, cre));
        Assert.False(h.Game.CanAfford(h.P1, cre));
        Assert.Throws<RuleViolationException>(() => h.Game.SummonCreature(0));
    }

    [Fact]
    public void CostDecrease_IsFlooredAtOne()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutCreature(h.P1, CardFactory.Creature(1, 1000, Civilization.Nature, "Big Discount",
            CardFactory.Eff(EffectId.CostDecrease_Summon_All, value: 5)));
        var cre = CardFactory.Creature(2, 3000, Civilization.Nature, "Cheap");
        h.PutInHand(h.P1, cre);

        Assert.Equal(1, h.Game.CardManaCost(h.P1, cre));
    }

    // ---------------------------------------------------- spell-gated summon

    [Fact]
    public void SummonRequiresSpellCast_BlocksUntilASpellIsCast()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Darkness, 3);
        var cre = CardFactory.Creature(2, 3000, Civilization.Darkness, "Spell Binder",
            Keyword.SummonRequiresSpellCast);
        var spell = CardFactory.Spell(1, Civilization.Darkness, "Probe", CardFactory.Eff(EffectId.Spell_Draw, value: 1));
        h.PutInHand(h.P1, spell);
        h.PutInHand(h.P1, cre);

        Assert.False(h.Game.CanSummon(h.P1, cre));
        Assert.Throws<RuleViolationException>(() => h.Game.SummonCreature(0));

        h.Game.CastSpell(0); // the probe counts as "cast a spell this turn"
        Assert.True(h.Game.CanSummon(h.P1, cre));

        // The spell binder is now first in hand (index 0 after the probe resolved and left).
        h.Game.SummonCreature(0);
        Assert.Contains(cre.Id, h.P1.BattleZone.Select(x => x.Card.Id));
    }
}