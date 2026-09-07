using System.Linq;
using DuelMasters.Domain;
using DuelMasters.Domain.Ai;
using Xunit;

namespace DuelMasters.Domain.Tests;

/// <summary>
/// Evolution creatures: they cannot be summoned normally and instead are placed on
/// top of one of their owner's creatures of a matching race (they CAN still be
/// charged to the mana zone like any other hand card). The whole stack counts as
/// the top card; when the top leaves the battle zone everything underneath is
/// destroyed to the graveyard (without triggering).
/// </summary>
public class EvolutionTests
{
    private static Card HeroBase(string name, int power = 2000) =>
        CardFactory.CreatureWithRace(1, power, Civilization.Fire, name, "Hero");

    private static Card HeroEvolution(string name, int power = 5000, int cost = 5, System.Collections.Generic.IEnumerable<CardEffect>? effects = null, params Keyword[] keywords) =>
        CardFactory.Evolution(cost, power, Civilization.Fire, name, "Hero", "Hero", effects, keywords);

    [Fact]
    public void Evolve_MatchingBase_IsFreeAndPlacesTheStackOnTop()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var baseCard = h.PutCreature(h.P1, HeroBase("Keeper"));
        var evoCard = HeroEvolution("Knight");
        h.PutInHand(h.P1, evoCard);

        var top = h.Game.EvolveCreature(0, 0);

        Assert.Same(evoCard, top.Card);
        Assert.Single(h.P1.BattleZone);
        Assert.Same(top, h.P1.BattleZone[0]);
        Assert.DoesNotContain(baseCard, h.P1.BattleZone);
        Assert.Contains(baseCard, top.Underneath);
        Assert.Equal(Zone.Underneath, baseCard.Zone);
        Assert.True(top.IsSummoningSick);
        Assert.False(top.IsTapped);
        Assert.Empty(h.P1.Hand);
        Assert.Empty(h.P1.ManaZone); // no mana was spent
    }

    [Fact]
    public void Evolve_SpeedAttacker_CanAttackImmediately_WithTheTopsPower()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutCreature(h.P1, HeroBase("Keeper"));
        var evoCard = HeroEvolution("Knight", power: 5000, keywords: Keyword.SpeedAttacker);
        h.PutInHand(h.P1, evoCard);
        var victim = h.PutCreature(h.P2, CardFactory.Creature(1, 3000, Civilization.Fire, "Victim"), tapped: true);

        var top = h.Game.EvolveCreature(0, 0);
        Assert.False(top.IsSummoningSick);

        h.Game.AttackCreature(0, 0);
        Assert.DoesNotContain(victim, h.P2.BattleZone);
        Assert.Contains(top, h.P1.BattleZone);
    }

    [Fact]
    public void Evolve_WrongRace_ThrowsAndChangesNothing()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutCreature(h.P1, CardFactory.CreatureWithRace(1, 2000, Civilization.Fire, "Ranger", "Ranger"));
        var evoCard = HeroEvolution("Knight");
        var evoInst = h.PutInHand(h.P1, evoCard);

        Assert.Throws<RuleViolationException>(() => h.Game.EvolveCreature(0, 0));
        Assert.Contains(evoInst, h.P1.Hand);
        Assert.Empty(h.P1.BattleZone[0].Underneath);
        Assert.Single(h.P1.BattleZone);
    }

    [Fact]
    public void Evolve_NonEvolutionCard_Throws()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutCreature(h.P1, HeroBase("Keeper"));
        h.PutInHand(h.P1, HeroBase("Plain"));

        Assert.Throws<RuleViolationException>(() => h.Game.EvolveCreature(0, 0));
    }

    [Fact]
    public void CanEvolve_RequiresBase_AndCostsNothing()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var lone = HeroEvolution("Lone");
        var loneInst = h.PutInHand(h.P1, lone);

        Assert.False(h.Game.HasEvolutionBase(h.P1, lone));
        Assert.False(h.Game.CanEvolve(h.P1, lone));

        h.PutCreature(h.P1, HeroBase("Keeper"));
        Assert.True(h.Game.HasEvolutionBase(h.P1, lone));
        Assert.True(h.Game.CanEvolve(h.P1, lone)); // free - no affinity/mana needed
        Assert.Contains(loneInst, h.P1.Hand);
    }

    [Fact]
    public void Evolution_CannotBeSummoned_ButCanBeChargedToMana()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var evoCard = HeroEvolution("Knight");
        var evoInst = h.PutInHand(h.P1, evoCard);

        Assert.False(h.Game.CanSummon(h.P1, evoCard));
        Assert.Throws<RuleViolationException>(() => h.Game.SummonCreature(0));

        h.Game.PlayManaToManaZone(0);

        Assert.Contains(evoInst, h.P1.ManaZone);
        Assert.Empty(h.P1.Hand);
        Assert.True(h.Game.ManaChargedThisTurn);
    }

    [Fact]
    public void Evolve_TriggersOnPlayAbilities()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutCreature(h.P1, HeroBase("Keeper"));
        var evoCard = HeroEvolution("Knight", effects: new[] { CardFactory.Eff(EffectId.OnPlay_Draw, value: 1) });
        h.PutInHand(h.P1, evoCard);

        h.Game.EvolveCreature(0, 0);

        Assert.Single(h.P1.Hand); // the evo was used and one card drawn
    }

    [Fact]
    public void DestroyEvolution_UnderneathGoesToGraveyardWithTheTop()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var baseCard = h.PutCreature(h.P1, HeroBase("Keeper"));
        h.PutInHand(h.P1, HeroEvolution("Knight", power: 2000, cost: 3));
        var top = h.Game.EvolveCreature(0, 0);

        var removal = CardFactory.Spell(1, Civilization.Darkness, "Raze",
            CardFactory.Eff(EffectId.Spell_DestroyPowerAtMost, EffectTargetScope.OpponentCreature, value: 2000));
        var removalInst = h.PutInHand(h.P2, removal);
        h.PutMana(h.P2, CardFactory.Creature(1, 1000, Civilization.Darkness, "DarkMana"));
        h.Game.EndMainPhase();
        h.Game.EndTurn();
        h.Game.StartTurn();
        h.Game.Draw();

        h.Game.CastSpell(h.P2.Hand.IndexOf(removalInst), h.P1, 0);

        Assert.Empty(h.P1.BattleZone);
        Assert.Contains(top, h.P1.Graveyard);
        Assert.Contains(baseCard, h.P1.Graveyard);
        Assert.Equal(Zone.Graveyard, baseCard.Zone);
        Assert.Empty(top.Underneath);
    }

    [Fact]
    public void ReturnToHand_EvolutionBounce_SendsUnderneathToGraveyard()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var baseCard = h.PutCreature(h.P1, HeroBase("Keeper"));
        h.PutInHand(h.P1, HeroEvolution("Knight", power: 2000, cost: 3));
        var top = h.Game.EvolveCreature(0, 0);

        var bounce = CardFactory.Spell(1, Civilization.Water, "Spiral Gate",
            CardFactory.Eff(EffectId.Spell_ReturnToHand, EffectTargetScope.OpponentCreature));
        var bounceInst = h.PutInHand(h.P2, bounce);
        h.PutMana(h.P2, CardFactory.Creature(1, 1000, Civilization.Water, "WaterMana"));
        h.Game.EndMainPhase();
        h.Game.EndTurn();
        h.Game.StartTurn();
        h.Game.Draw();

        h.Game.CastSpell(h.P2.Hand.IndexOf(bounceInst), h.P1, 0);

        Assert.Empty(h.P1.BattleZone);
        Assert.Contains(top, h.P1.Hand);          // only the top returns to hand
        Assert.Contains(baseCard, h.P1.Graveyard);
        Assert.Equal(Zone.Graveyard, baseCard.Zone);
    }

    [Fact]
    public void DestroyEvolution_OnDestroyedReplacementSavesOnlyTheTop()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var baseCard = h.PutCreature(h.P1, HeroBase("Keeper"));
        h.PutInHand(h.P1, HeroEvolution("Knight", power: 2000, cost: 3,
            effects: new[] { CardFactory.Eff(EffectId.OnDestroyed_ToHand) }));
        var top = h.Game.EvolveCreature(0, 0);

        var removal = CardFactory.Spell(1, Civilization.Darkness, "Raze",
            CardFactory.Eff(EffectId.Spell_DestroyPowerAtMost, EffectTargetScope.OpponentCreature, value: 2000));
        var removalInst = h.PutInHand(h.P2, removal);
        h.PutMana(h.P2, CardFactory.Creature(1, 1000, Civilization.Darkness, "DarkMana"));
        h.Game.EndMainPhase();
        h.Game.EndTurn();
        h.Game.StartTurn();
        h.Game.Draw();

        h.Game.CastSpell(h.P2.Hand.IndexOf(removalInst), h.P1, 0);

        Assert.Empty(h.P1.BattleZone);
        Assert.Contains(top, h.P1.Hand);          // replacement protects the top only
        Assert.Contains(baseCard, h.P1.Graveyard);
    }

    [Fact]
    public void DestroyEvolution_StackOnStack_CascadesEverything()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var baseCard = h.PutCreature(h.P1, HeroBase("Keeper"));
        var mid = new CardInstance(CardFactory.Evolution(3, 4000, Civilization.Fire, "Old Top", "Hero", "Hero"), h.P1)
        {
            Zone = Zone.Underneath,
        };
        baseCard.Underneath.Add(mid);
        h.PutInHand(h.P1, HeroEvolution("New Top", power: 6000, cost: 6));
        var top = h.Game.EvolveCreature(0, 0);
        Assert.Equal(2, top.Underneath.Count); // the entire stack slid under the new top

        var removal = CardFactory.Spell(1, Civilization.Darkness, "Raze",
            CardFactory.Eff(EffectId.Spell_DestroyPowerAtMost, EffectTargetScope.OpponentCreature, value: 6000));
        var removalInst = h.PutInHand(h.P2, removal);
        h.PutMana(h.P2, CardFactory.Creature(1, 1000, Civilization.Darkness, "DarkMana"));
        h.Game.EndMainPhase();
        h.Game.EndTurn();
        h.Game.StartTurn();
        h.Game.Draw();

        h.Game.CastSpell(h.P2.Hand.IndexOf(removalInst), h.P1, 0);

        Assert.Contains(top, h.P1.Graveyard);
        Assert.Contains(baseCard, h.P1.Graveyard);
        Assert.Contains(mid, h.P1.Graveyard);
        Assert.Empty(h.P1.BattleZone);
    }

    [Fact]
    public void Ai_PlaysAnEvolutionOntoItsBase_InsteadOfSummoningIt()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutCreature(h.P1, HeroBase("Keeper"));
        var evoCard = HeroEvolution("Knight");
        var evoInst = h.PutInHand(h.P1, evoCard);

        var ai = new AiController(h.P1);
        ai.PlayTurn(h.Game);

        var top = Assert.Single(h.P1.BattleZone);
        Assert.Same(evoCard, top.Card);
        Assert.Single(top.Underneath);
        Assert.DoesNotContain(evoInst, h.P1.Hand);
    }
}