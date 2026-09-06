using System;
using System.Linq;
using DuelMasters.Domain;
using DuelMasters.Domain.Ai;
using Xunit;

namespace DuelMasters.Domain.Tests;

public class AiTests
{
    [Fact]
    public void Step_OutsideAiTurn_Throws()
    {
        var h = GameHarness.AtMainPhase();
        var ai = new AiController(h.P2);
        Assert.Throws<InvalidOperationException>(() => ai.Step(h.Game));
    }

    [Fact]
    public void Step_FirstActionChargesOneMana_WhenNothingPlayable()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutInHand(h.P1, CardFactory.Creature(9, 1000, Civilization.Fire, "Big"));
        h.PutInHand(h.P1, CardFactory.Creature(8, 1000, Civilization.Fire, "Bigger"));
        var ai = new AiController(h.P1);

        var step = ai.Step(h.Game);

        Assert.Equal(AiStepKind.ActionTaken, step.Kind);
        Assert.True(h.Game.ManaChargedThisTurn);
        Assert.Single(h.P1.ManaZone);
        Assert.Single(h.P1.Hand);
    }

    [Fact]
    public void Step_SummonsThenAttacks_AndRespectsSummoningSickness()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        foreach (var _ in Enumerable.Range(0, 3))
            h.PutMana(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "FireMana"));
        h.PutInHand(h.P1, CardFactory.Creature(1, 2000, Civilization.Fire, "Beast"));
        h.PutInHand(h.P1, CardFactory.Creature(9, 1000, Civilization.Fire, "Expensive"));
        h.SetShields(h.P2, CardFactory.Creature(0, 1000, Civilization.Fire, "Shield1"),
            CardFactory.Creature(0, 1000, Civilization.Fire, "Shield2"),
            CardFactory.Creature(0, 1000, Civilization.Fire, "Shield3"),
            CardFactory.Creature(0, 1000, Civilization.Fire, "Shield4"),
            CardFactory.Creature(0, 1000, Civilization.Fire, "Shield5"));
        var ai = new AiController(h.P1);

        // Turn 1: the AI dumps the uncastable card as mana, then summons the Beast.
        Assert.Equal(AiStepKind.ActionTaken, ai.Step(h.Game).Kind); // mana charge
        Assert.Equal(AiStepKind.ActionTaken, ai.Step(h.Game).Kind); // summon
        Assert.Single(h.P1.BattleZone);
        Assert.True(h.P1.BattleZone[0].IsSummoningSick);
        Assert.Equal(AiStepKind.TurnEnded, ai.Step(h.Game).Kind); // sick -> no attack

        // Advance P2's turn and return to P1; the Beast is now ready and swings.
        h.Game.EndMainPhase();
        h.Game.EndTurn();
        h.Game.StartTurn();
        h.Game.Draw();
        h.Game.EndMainPhase();
        h.Game.EndTurn();
        h.Game.StartTurn();
        h.Game.Draw();

        Assert.False(h.P1.BattleZone[0].IsSummoningSick);
        Assert.Equal(5, h.P2.ShieldCount);
        Assert.Equal(AiStepKind.ActionTaken, ai.Step(h.Game).Kind); // charges the drawn filler
        Assert.Equal(AiStepKind.ActionTaken, ai.Step(h.Game).Kind); // swings for a shield
        Assert.Equal(4, h.P2.ShieldCount);
        Assert.True(h.Game.HasAttackedThisTurn);
        Assert.Equal(AiStepKind.TurnEnded, ai.Step(h.Game).Kind);
    }

    [Fact]
    public void DecideBlock_InterceptsFavourableAttacker()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutCreature(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "WeakAttacker"), sick: false);
        h.PutCreature(h.P2, CardFactory.Creature(1, 3000, Civilization.Fire, "StrongBlocker", Keyword.Blocker), sick: false);
        var ai = new AiController(h.P2);

        var blocks = ai.DecideBlock(h.Game, attackerIndex: 0, out var blockerIndex);

        Assert.True(blocks);
        Assert.Equal(0, blockerIndex);
        h.Game.AttackPlayer(0, h.P2, blockerIndex);
        Assert.Empty(h.P1.BattleZone); // weak attacker dies
        Assert.Single(h.P2.BattleZone); // blocker survives
        Assert.True(h.P2.BattleZone[0].IsTapped);
    }

    [Fact]
    public void DecideBlock_RefusesUnfavourableAttacker()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutCreature(h.P1, CardFactory.Creature(1, 3000, Civilization.Fire, "StrongAttacker"), sick: false);
        h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "WeakBlocker", Keyword.Blocker), sick: false);
        var ai = new AiController(h.P2);

        Assert.False(ai.DecideBlock(h.Game, attackerIndex: 0, out _));
    }

    [Fact]
    public void PlayTurn_EndsTurnNormally_AndSwitchesActivePlayer()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutInHand(h.P1, CardFactory.Creature(1, 2000, Civilization.Fire, "Beast"));
        var ai = new AiController(h.P1);

        ai.PlayTurn(h.Game);

        Assert.Equal(GamePhase.Untap, h.Game.Phase);
        Assert.Same(h.P2, h.Game.ActivePlayer);
    }

    [Fact]
    public void Step_CastsRemovalSpell_OnStrongestFoeCreature()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        foreach (var _ in Enumerable.Range(0, 4))
            h.PutMana(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "Mana"));
        h.PutInHand(h.P1, CardFactory.Creature(9, 1000, Civilization.Fire, "DeadCard"));
        h.PutInHand(h.P1, CardFactory.Spell(2, Civilization.Fire, "Bolt",
            CardFactory.Eff(EffectId.Spell_DestroyPowerAtMost, EffectTargetScope.OpponentCreature, 3000)));
        h.PutCreature(h.P2, CardFactory.Creature(1, 3000, Civilization.Fire, "Strong"), sick: false);
        h.PutCreature(h.P2, CardFactory.Creature(1, 2000, Civilization.Fire, "Weak"), sick: false);
        var ai = new AiController(h.P1);

        // Charge the dead filler, then cast Bolt at the strongest legal target.
        var step1 = ai.Step(h.Game);
        var spellStep = ai.Step(h.Game);

        Assert.Equal(AiStepKind.ActionTaken, step1.Kind);
        Assert.Equal(AiStepKind.ActionTaken, spellStep.Kind);
        Assert.Single(h.P2.BattleZone);
        Assert.Equal("Weak", h.P2.BattleZone[0].Card.Name);
        Assert.Single(h.P1.Graveyard);
    }

    [Fact]
    public void Step_DeclinesToCastRemovalSpell_WhenNoLegalTargetExists()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        foreach (var _ in Enumerable.Range(0, 4))
            h.PutMana(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "Mana"));
        h.PutInHand(h.P1, CardFactory.Creature(9, 1000, Civilization.Fire, "DeadCard"));
        h.PutInHand(h.P1, CardFactory.Spell(2, Civilization.Fire, "Bolt",
            CardFactory.Eff(EffectId.Spell_DestroyPowerAtMost, EffectTargetScope.OpponentCreature, 3000)));
        var ai = new AiController(h.P1);

        ai.Step(h.Game); // charge the filler
        var end = ai.Step(h.Game); // nothing to develop, nothing to attack

        Assert.Equal(AiStepKind.TurnEnded, end.Kind);
        Assert.True(h.P1.Hand[0].Card.Name == "Bolt"); // the removal spell stayed in hand
        Assert.Empty(h.P2.BattleZone);
    }

    [Fact]
    public void Step_WhenAttackerBreaksEnemyShieldTrigger_ReportsWaiting()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutCreature(h.P1, CardFactory.Creature(1, 1500, Civilization.Fire, "Rush"), sick: false);
        h.SetShields(h.P2, CardFactory.Creature(0, 1000, Civilization.Fire, "TriggerShield",
            CardFactory.Eff(EffectId.OnPlay_Draw, EffectTargetScope.None, 0), Keyword.ShieldTrigger));
        var ai = new AiController(h.P1);

        ai.Step(h.Game); // charges the opening hand filler
        var swing = ai.Step(h.Game); // attacks, breaks the trigger shield of the defender

        Assert.Equal(AiStepKind.WaitingOnShieldTriggers, swing.Kind);
        Assert.True(h.Game.ShieldTriggerWindowActive);
        Assert.Same(h.P2, h.Game.ShieldTriggerOwner);
        Assert.Equal(0, h.P2.ShieldCount);

        // The defender resolves (played vs declined); the attacker may then continue.
        h.Game.DeclineShieldTriggers();
        Assert.False(h.Game.ShieldTriggerWindowActive);
        Assert.Equal(AiStepKind.TurnEnded, ai.Step(h.Game).Kind); // no more attackers
    }

    [Fact]
    public void ResolveShieldTriggers_PlaysCreatureAndTargetsSpell_ThenDeclinesRest()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        h.PutCreature(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "Invader", Keyword.DoubleBreaker), sick: false);
        h.SetShields(h.P2,
            CardFactory.Creature(2, 2500, Civilization.Fire, "Sentinel",
                CardFactory.Eff(EffectId.OnPlay_Draw, EffectTargetScope.None, 0), Keyword.ShieldTrigger),
            CardFactory.Spell(3, Civilization.Fire, "Wrath",
                CardFactory.Eff(EffectId.Spell_DestroyPowerAtMost, EffectTargetScope.OpponentCreature, 1000),
                Keyword.ShieldTrigger),
            CardFactory.Spell(2, Civilization.Water, "Idle",
                CardFactory.Eff(EffectId.Spell_Draw, EffectTargetScope.None, 0), Keyword.ShieldTrigger));
        var ai = new AiController(h.P2);

        h.Game.AttackPlayer(0); // P1 breaks two P2 shields
        Assert.True(h.Game.ShieldTriggerWindowActive);

        ai.ResolveShieldTriggers(h.Game);

        // Sentinel entered the battle zone for free (sick, drawing a card).
        Assert.False(h.Game.ShieldTriggerWindowActive);
        Assert.Single(h.P2.BattleZone);
        Assert.Equal("Sentinel", h.P2.BattleZone[0].Card.Name);
        Assert.True(h.P2.BattleZone[0].IsSummoningSick);
        // Wrath was cast at the only legal foe creature (1000 power), destroying it.
        Assert.Empty(h.P1.BattleZone);
        Assert.Single(h.P1.Graveyard);
        Assert.Equal("Invader", h.P1.Graveyard.Single().Card.Name);
        Assert.Equal("Wrath", h.P2.Graveyard.Single().Card.Name);
        Assert.Equal(CardType.Spell, h.P2.Graveyard.Single().Card.CardType);
        Assert.False(h.Game.ShieldTriggerWindowActive);
    }

    [Fact]
    public void TwoAis_PlayManyDeterministicGames_ToCleanEndings()
    {
        // Identical 40-card decks, no shuffle, same RNG each run -> reproducible.
        for (var run = 0; run < 8; run++)
        {
            var p1 = CardFactory.PlayerWithDeck("BotA", 40);
            var p2 = CardFactory.PlayerWithDeck("BotB", 40);
            var game = new DuelGame(p1, p2, new Random(run));
            var ai1 = new AiController(p1);
            var ai2 = new AiController(p2);

            game.StartGame(shuffle: false);
            var guard = 0;
            while (!game.IsGameOver && guard++ < 3000)
            {
                game.StartTurn();
                game.Draw();
                var ai = ReferenceEquals(game.ActivePlayer, p1) ? ai1 : ai2;
                ai.PlayTurn(game);
            }

            Assert.True(game.IsGameOver, $"run {run} never finished (stalled after {guard} turns)");
            Assert.NotNull(game.Winner);
            Assert.True(ReferenceEquals(game.Winner, p1) || ReferenceEquals(game.Winner, p2));

            Assert.Equal(40, ZoneCount(p1));
            Assert.Equal(40, ZoneCount(p2));
        }

        static int ZoneCount(Player p) =>
            p.Deck.Count + p.Shields.Count + p.Hand.Count + p.ManaZone.Count + p.BattleZone.Count + p.Graveyard.Count;
    }
}