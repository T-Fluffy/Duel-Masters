using System;
using System.Linq;
using DuelMasters.Domain;
using DuelMasters.Domain.Ai;
using Xunit;

namespace DuelMasters.Domain.Tests;

/// <summary>
/// Coverage for two DM-06 / DM-08 deferred effects:
///
/// * Crew Breaker (Q-tronic Gargantua): an extra broken shield per other creature
///   of the rider's race.
/// * The four "you may ..." attack triggers (Migalo, Vikorakys, Gachack,
///   Slaphappy): win-or-lose choices that pause the attack until answered.
/// </summary>
public class CrewBreakerAndAttackDecisionTests
{
    private static int _serial;

    private static Card Breaker(string name, string race, int power = 5000, params Keyword[] keywords)
        => CardFactory.CreatureWithRace(6, power, Civilization.Darkness, $"{name}-{++_serial}", race,
            new[] { CardFactory.Eff(EffectId.Breaker_PerOtherRace, data: race) }, keywords);

    private static Card Creature(int power, Civilization civ, string name, string race = "R", params Keyword[] keywords)
        => CardFactory.CreatureWithRace(1, power, civ, $"{name}-{++_serial}", race,
            System.Array.Empty<CardEffect>(), keywords);

    private static Card MayShields(int value = 2, int power = 4000)
        => CardFactory.CreatureWithRace(4, power, Civilization.Light, $"Migalo-{++_serial}", "R",
            new[] { CardFactory.Eff(EffectId.AttackTrigger_MayLookAtShields, value: value) });

    private static Card MaySearch(int power = 4000)
        => CardFactory.CreatureWithRace(4, power, Civilization.Water, $"Vikorakys-{++_serial}", "R",
            new[] { CardFactory.Eff(EffectId.AttackTrigger_MaySearchToHand) });

    private static Card UnblockedDestroy(int power = 5000)
        => CardFactory.CreatureWithRace(5, power, Civilization.Fire, $"Gachack-{++_serial}", "R",
            new[] { CardFactory.Eff(EffectId.AttackTrigger_UnblockedMayDestroy) });

    private static Card AtMostDestroy(int cap, int power = 4000)
        => CardFactory.CreatureWithRace(4, power, Civilization.Darkness, $"Slaphappy-{++_serial}", "R",
            new[] { CardFactory.Eff(EffectId.AttackTrigger_MayDestroyPowerAtMost, value: cap) });

    // ------------------------------------------------------------- crew breaker

    [Fact]
    public void CrewBreaker_BreaksOneExtraShieldPerOtherSurvivor()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var gargantua = h.PutCreature(h.P1, Breaker("Q-tronic", "Survivor", 6000));
        h.PutCreature(h.P1, Creature(1000, Civilization.Darkness, "Sentry", "Survivor"));
        h.PutCreature(h.P1, Creature(1000, Civilization.Darkness, "Warden", "Survivor"));
        h.PutCreature(h.P1, Creature(1000, Civilization.Fire, "Not a survivor", "Outcast"));
        h.SetShields(h.P2, Enumerable.Range(0, 5).Select(
            n => CardFactory.Creature(1, 1000, Civilization.Light, $"Shield{n}")).ToArray());
        var shieldCount = h.P2.ShieldCount;

        h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(gargantua));

        Assert.Equal(shieldCount - 3, h.P2.ShieldCount);
    }

    [Fact]
    public void CrewBreaker_CountsOnlyTheControllersOtherCreatures()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var gargantua = h.PutCreature(h.P1, Breaker("Q-tronic", "Survivor", 6000));
        // The opponent's two Survivors do not feed the rider.
        h.PutCreature(h.P2, Creature(1000, Civilization.Darkness, "Foe sentry", "Survivor"));
        h.PutCreature(h.P2, Creature(1000, Civilization.Darkness, "Foe warden", "Survivor"));
        h.SetShields(h.P2, Enumerable.Range(0, 4).Select(
            n => CardFactory.Creature(1, 1000, Civilization.Light, $"Shield{n}")).ToArray());
        var before = h.P2.ShieldCount;

        h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(gargantua));

        Assert.Equal(before - 1, h.P2.ShieldCount);
    }

    [Fact]
    public void CrewBreaker_DoesNotCountItself()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var gargantua = h.PutCreature(h.P1, Breaker("Q-tronic", "Survivor", 6000));
        h.SetShields(h.P2, Enumerable.Range(0, 4).Select(
            n => CardFactory.Creature(1, 1000, Civilization.Light, $"Shield{n}")).ToArray());
        var before = h.P2.ShieldCount;

        h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(gargantua));

        Assert.Equal(before - 1, h.P2.ShieldCount);
    }

    // ---------------------------------------------------- shield-look may choice

    [Fact]
    public void Migalo_LooksAndPutsBack_ThenTheAttackStillBreaksShields()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var migalo = h.PutCreature(h.P1, MayShields(2));
        h.SetShields(h.P2, CardFactory.Creature(1, 1000, Civilization.Light, "S1"),
            CardFactory.Creature(1, 1000, Civilization.Light, "S2"),
            CardFactory.Creature(1, 1000, Civilization.Light, "S3"));

        h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(migalo));

        // The attack paused at the choice: no shields are broken yet.
        Assert.True(h.Game.AttackDecisionWindowActive);
        Assert.Equal(DuelGame.AttackDecisionKind.LookAtShields, h.Game.PendingAttackDecision);
        Assert.Equal(3, h.P2.ShieldCount);

        h.Game.AcceptAttackLookAtShields(new[] { 0, 2 });

        // The peek is informational: the shield zone is untouched by it, and the
        // direct attack then continues to its break step (3 -> 2).
        Assert.False(h.Game.AttackDecisionWindowActive);
        Assert.Equal(2, h.P2.ShieldCount);
    }

    [Fact]
    public void Migalo_LookRejectsWrongShieldCount()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var migalo = h.PutCreature(h.P1, MayShields(2));
        h.SetShields(h.P2, CardFactory.Creature(1, 1000, Civilization.Light, "S1"),
            CardFactory.Creature(1, 1000, Civilization.Light, "S2"),
            CardFactory.Creature(1, 1000, Civilization.Light, "S3"));

        h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(migalo));

        var ex = Assert.Throws<RuleViolationException>(() => h.Game.AcceptAttackLookAtShields(new[] { 0 }));
        Assert.Contains("exactly 2", ex.Message);
        Assert.True(h.Game.AttackDecisionWindowActive); // still open, retry allowed
        Assert.Equal(3, h.P2.ShieldCount);

        h.Game.AcceptAttackLookAtShields(new[] { 1, 2 });
        Assert.False(h.Game.AttackDecisionWindowActive);
    }

    [Fact]
    public void Migalo_DeclineMovesStraightToTheHit()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var migalo = h.PutCreature(h.P1, MayShields(2));
        h.SetShields(h.P2, CardFactory.Creature(1, 1000, Civilization.Light, "S1"),
            CardFactory.Creature(1, 1000, Civilization.Light, "S2"),
            CardFactory.Creature(1, 1000, Civilization.Light, "S3"));

        h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(migalo));
        h.Game.DeclineAttackDecision();

        Assert.False(h.Game.AttackDecisionWindowActive);
        Assert.Equal(2, h.P2.ShieldCount);
    }

    // --------------------------------------------------- deck-search may choice

    [Fact]
    public void Vikorakys_TakesTheStrongestCard_ThenShuffles()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var vikorakys = h.PutCreature(h.P1, MaySearch());
        h.P1.Deck.Clear();
        var weak = CardFactory.Spell(2, Civilization.Water, "Twig");
        var strong = CardFactory.Creature(9, 9000, Civilization.Water, "Titan");
        h.P1.Deck.AddRange(new[] { weak, strong, weak, weak });

        var handBefore = h.P1.Hand.Count;
        h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(vikorakys));

        Assert.True(h.Game.AttackDecisionWindowActive);
        Assert.Equal(DuelGame.AttackDecisionKind.SearchToHand, h.Game.PendingAttackDecision);

        h.Game.AcceptAttackSearchToHand();

        Assert.False(h.Game.AttackDecisionWindowActive);
        Assert.Equal(handBefore + 1, h.P1.Hand.Count);
        Assert.Contains(h.P1.Hand, c => c.Card == strong);
        Assert.DoesNotContain(h.P1.Deck, c => c == strong);
        // The three filler cards remain in the (shuffled) deck.
        Assert.Equal(3, h.P1.Deck.Count(c => c == weak));
    }

    // --------------------------------------------------- unblocked destroy choice

    [Fact]
    public void Gachack_UnblockedDestroy_DestroysTheNamedCreatureThenBreaksShields()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var gachack = h.PutCreature(h.P1, UnblockedDestroy());
        var foe = h.PutCreature(h.P2, Creature(4000, Civilization.Nature, "Biggie"));
        h.SetShields(h.P2, CardFactory.Creature(1, 1000, Civilization.Light, "S1"),
            CardFactory.Creature(1, 1000, Civilization.Light, "S2"));

        h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(gachack));

        // Unblocked triggers fire after the shield break, so the hit already landed.
        Assert.True(h.Game.AttackDecisionWindowActive);
        Assert.Equal(DuelGame.AttackDecisionKind.DestroyCreature, h.Game.PendingAttackDecision);
        Assert.Equal(1, h.P2.ShieldCount);

        h.Game.AcceptAttackDestroy(h.P2, h.P2.BattleZone.IndexOf(foe));

        Assert.False(h.Game.AttackDecisionWindowActive);
        Assert.Equal(Zone.Graveyard, foe.Zone);
    }

    [Fact]
    public void Gachack_CanDestroyAnOwnCreature_AndTheWinChoiceClosesTheWindow()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var gachack = h.PutCreature(h.P1, UnblockedDestroy());
        var ally = h.PutCreature(h.P1, Creature(1500, Civilization.Fire, "Ally"));
        h.SetShields(h.P2, CardFactory.Creature(1, 1000, Civilization.Light, "S"));
        h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(gachack));

        Assert.Contains(ally, h.Game.AttackDecisionTargets);
        Assert.Contains(gachack, h.Game.AttackDecisionTargets);

        h.Game.AcceptAttackDestroy(h.P1, h.P1.BattleZone.IndexOf(ally));
        Assert.Equal(Zone.Graveyard, ally.Zone);
    }

    [Fact]
    public void Gachack_DestroyChoice_RejectsATargetOutsideThePool()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var gachack = h.PutCreature(h.P1, UnblockedDestroy());
        var foe = h.PutCreature(h.P2, Creature(3000, Civilization.Nature, "Biggie"));
        h.SetShields(h.P2, CardFactory.Creature(1, 1000, Civilization.Light, "S1"),
            CardFactory.Creature(1, 1000, Civilization.Light, "S2"));

        h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(gachack));
        var ally = h.PutCreature(h.P1, Creature(1500, Civilization.Fire, "Late ally"));

        // The pool was frozen when the window opened; the newcomer is not legal.
        var ex = Assert.Throws<RuleViolationException>(() =>
            h.Game.AcceptAttackDestroy(h.P1, h.P1.BattleZone.IndexOf(ally)));
        Assert.Contains("not a legal target", ex.Message);
        Assert.True(h.Game.AttackDecisionWindowActive);
    }

    // ------------------------------------------------- power-at-most destroy choice

    [Fact]
    public void Slaphappy_TargetsOnlyOpponentCreaturesAtOrBelowTheCap()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var slappy = h.PutCreature(h.P1, AtMostDestroy(4000));
        var bug = h.PutCreature(h.P2, Creature(2000, Civilization.Darkness, "Bug"));
        var small = h.PutCreature(h.P2, Creature(4000, Civilization.Darkness, "On the dot"));
        var titan = h.PutCreature(h.P2, Creature(9000, Civilization.Darkness, "Titan"));
        h.SetShields(h.P2, CardFactory.Creature(1, 1000, Civilization.Light, "S1"),
            CardFactory.Creature(1, 1000, Civilization.Light, "S2"));

        h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(slappy));

        Assert.True(h.Game.AttackDecisionWindowActive);
        Assert.Equal(DuelGame.AttackDecisionKind.DestroyPowerAtMost, h.Game.PendingAttackDecision);
        Assert.Equal(new[] { bug, small }, h.Game.AttackDecisionTargets);
        Assert.DoesNotContain(titan, h.Game.AttackDecisionTargets);

        var ex = Assert.Throws<RuleViolationException>(() =>
            h.Game.AcceptAttackDestroy(h.P2, h.P2.BattleZone.IndexOf(titan)));
        Assert.Contains("not a legal target", ex.Message);

        h.Game.AcceptAttackDestroy(h.P2, h.P2.BattleZone.IndexOf(small));
        Assert.Equal(Zone.Graveyard, small.Zone);
        Assert.Equal(Zone.BattleZone, titan.Zone);
    }

    [Fact]
    public void Slaphappy_WindowNeverOpensWithoutALegalVictim()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var slappy = h.PutCreature(h.P1, AtMostDestroy(4000));
        h.PutCreature(h.P2, Creature(9000, Civilization.Darkness, "Titan"));
        h.SetShields(h.P2, CardFactory.Creature(1, 1000, Civilization.Light, "S"));
        h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(slappy));

        Assert.False(h.Game.AttackDecisionWindowActive);
        Assert.Equal(0, h.P2.ShieldCount);
    }

    // ---------------------------------------------------------- window gating

    [Fact]
    public void MayWindow_GatesAllOtherActions()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var migalo = h.PutCreature(h.P1, MayShields(2));
        var mage = h.PutCreature(h.P1, CardFactory.TapCreature(3, 3000, Civilization.Water, "Mage",
            CardFactory.Eff(EffectId.Tap_Draw, value: 1)));
        var card = h.PutInHand(h.P1, CardFactory.Creature(2, 2000, Civilization.Fire, "Summon me"));
        h.SetShields(h.P2, CardFactory.Creature(1, 1000, Civilization.Light, "S1"),
            CardFactory.Creature(1, 1000, Civilization.Light, "S2"),
            CardFactory.Creature(1, 1000, Civilization.Light, "S3"));
        h.P1.Deck.Clear();
        for (var i = 0; i < 5; i++)
            h.P1.Deck.Add(CardFactory.Creature(1, 1000, Civilization.Water, $"Deck{i}"));

        h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(migalo));
        Assert.True(h.Game.AttackDecisionWindowActive);

        Assert.False(h.Game.CanUseTapAbility(h.P1, h.P1.BattleZone.IndexOf(mage)));
        Assert.Throws<RuleViolationException>(() => h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(mage)));
        Assert.Throws<RuleViolationException>(() => h.Game.SummonCreature(h.P1.Hand.IndexOf(card)));
        Assert.Throws<RuleViolationException>(() => h.Game.PlayManaToManaZone(h.P1.Hand.IndexOf(card)));
        Assert.Throws<RuleViolationException>(() => h.Game.EndMainPhase());

        h.Game.DeclineAttackDecision();
        Assert.False(h.Game.AttackDecisionWindowActive);
    }

    [Fact]
    public void AnswerWithNoWindowOpen_Throws()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Assert.Throws<RuleViolationException>(() => h.Game.DeclineAttackDecision());
        Assert.Throws<RuleViolationException>(() => h.Game.AcceptAttackSearchToHand());
    }

    // ------------------------------------------------------ AI may-choice policy

    [Fact]
    public void Ai_Gachack_DestroysTheStrongestFoe()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var gachack = h.PutCreature(h.P1, UnblockedDestroy(7000));
        var bug = h.PutCreature(h.P2, Creature(1500, Civilization.Nature, "Bug"));
        var big = h.PutCreature(h.P2, Creature(6000, Civilization.Nature, "Biggie"));
        for (var i = 0; i < 5; i++)
            h.P2.Shields.Add(CardFactory.Creature(1, 1000, Civilization.Light, $"Sh{i}"));

        new AiController(h.P1).PlayTurn(h.Game);

        Assert.Equal(Zone.Graveyard, big.Zone);
        Assert.Equal(Zone.BattleZone, bug.Zone);
    }

    [Fact]
    public void Ai_Gachack_DeclinesWhenNoFoeIsLegal()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var gachack = h.PutCreature(h.P1, UnblockedDestroy(7000));
        var own = h.PutCreature(h.P1, Creature(1200, Civilization.Fire, "Ally"));
        for (var i = 0; i < 5; i++)
            h.P2.Shields.Add(CardFactory.Creature(1, 1000, Civilization.Light, $"Sh{i}"));

        new AiController(h.P1).PlayTurn(h.Game);

        Assert.Equal(Zone.BattleZone, own.Zone);
    }

    [Fact]
    public void Ai_Vikorakys_TakesTheBestCard()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var vikorakys = h.PutCreature(h.P1, MaySearch(7000));
        h.P1.Deck.Clear();
        var weak = CardFactory.Spell(2, Civilization.Water, "Twig");
        var titan = CardFactory.Creature(9, 9000, Civilization.Water, "Titan");
        h.P1.Deck.AddRange(new[] { weak, titan, weak });
        for (var i = 0; i < 5; i++)
            h.P2.Shields.Add(CardFactory.Creature(1, 1000, Civilization.Light, $"Sh{i}"));

        var turn = new AiController(h.P1);
        // One Step reaches the attack (no charge/play possible with an empty hand),
        // which opens the may-search window; the next Step resolves it as the AI -
        // and may immediately charge the cheap... er, valuable searched card.
        turn.Step(h.Game);
        Assert.True(h.Game.AttackDecisionWindowActive);
        turn.Step(h.Game);

        // The best card left the deck; it ended up in the hand (via the search) and
        // only left the hand for the mana zone if the AI then charged it.
        Assert.Contains(h.P1.Hand.Concat(h.P1.ManaZone).Select(c => c.Card), c => c == titan);
        Assert.Equal(2, h.P1.Deck.Count(c => c == weak));
    }
}