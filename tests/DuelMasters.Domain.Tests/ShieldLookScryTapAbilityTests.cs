using System;
using System.Linq;
using DuelMasters.Domain;
using DuelMasters.Domain.Ai;
using DuelMasters.Domain.Networking;
using Xunit;

namespace DuelMasters.Domain.Tests;

/// <summary>
/// Coverage for the "choose a shield and look at it" (Tap_ChooseShieldLook,
/// e.g. Adomis) and "look at the top N cards of the deck and put them back in any
/// order" (Tap_ScryTopCards, e.g. Garatyano) tap abilities. The former is a pure
/// decision with no board change; the latter opens a blocking scry window that
/// must be resolved before the turn can continue.
/// </summary>
public class ShieldLookScryTapAbilityTests
{
    private static int _serial;

    private static Card ShieldLookCard(Civilization civ = Civilization.Light, int cost = 3)
        => CardFactory.TapCreature(cost, 2000, civ, $"Adomis-{++_serial}",
            CardFactory.Eff(EffectId.Tap_ChooseShieldLook), Keyword.CannotAttackPlayers);

    private static Card ScryCard(int count = 3, Civilization civ = Civilization.Water)
        => CardFactory.TapCreature(4, 2000, civ, $"Garatyano-{++_serial}",
            CardFactory.Eff(EffectId.Tap_ScryTopCards, value: count), Keyword.CannotAttackPlayers);

    // ------------------------------------------------------------------ shield look

    [Fact]
    public void ShieldLook_TapsTheCreature_AndLeavesTheShieldInPlace()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var adomis = h.PutCreature(h.P1, ShieldLookCard());
        var s1 = CardFactory.Creature(1, 1000, Civilization.Light, "ShieldOne");
        var s2 = CardFactory.Creature(1, 1000, Civilization.Light, "ShieldTwo");
        h.SetShields(h.P1, s1, s2);

        var idx = h.P1.BattleZone.IndexOf(adomis);
        Assert.True(h.Game.CanUseTapAbility(h.P1, idx));

        h.Game.ActivateTapAbilityShield(idx, 0);

        // Tapping is the whole cost: the inspected shield stays exactly where it
        // was, face-down, and is not added to the hand or any other zone.
        Assert.True(adomis.IsTapped);
        Assert.Equal(2, h.P1.ShieldCount);
        Assert.Same(s1, h.P1.Shields[0]);
        Assert.Same(s2, h.P1.Shields[1]);
        Assert.DoesNotContain(h.P1.Hand, c => c.Card == s1);
    }

    [Fact]
    public void ShieldLook_RejectsAShieldOutsideTheShieldZone()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var adomis = h.PutCreature(h.P1, ShieldLookCard());
        h.SetShields(h.P1, CardFactory.Creature(1, 1000, Civilization.Light, "S"));

        var ex = Assert.Throws<RuleViolationException>(() => h.Game.ActivateTapAbilityShield(h.P1.BattleZone.IndexOf(adomis), 4));
        Assert.Contains("shield", ex.Message);
        Assert.False(adomis.IsTapped); // the tap was not paid on failure
    }

    [Fact]
    public void ShieldLook_RequiresThePlayerToHaveShields()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var adomis = h.PutCreature(h.P1, ShieldLookCard());

        Assert.False(h.Game.CanUseTapAbility(h.P1, h.P1.BattleZone.IndexOf(adomis)));
        var ex = Assert.Throws<RuleViolationException>(() => h.Game.ActivateTapAbilityShield(h.P1.BattleZone.IndexOf(adomis), 0));
        Assert.Contains("shield", ex.Message);
        Assert.False(adomis.IsTapped);
    }

    [Fact]
    public void ShieldLook_GenericActivationPath_RefusesWithoutAShieldChoice()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var adomis = h.PutCreature(h.P1, ShieldLookCard());
        h.SetShields(h.P1, CardFactory.Creature(1, 1000, Civilization.Light, "S"));

        var ex = Assert.Throws<RuleViolationException>(() => h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(adomis)));
        Assert.Contains("shield choice", ex.Message);
        Assert.False(adomis.IsTapped);
    }

    // --------------------------------------------------------------- scry window

    [Fact]
    public void Scry_OpensAWindowWithTheTopNCards_AndSubmitsACustomOrder()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var garatyano = h.PutCreature(h.P1, ScryCard(count: 3));
        h.P1.Deck.Clear();
        var c1 = CardFactory.Creature(1, 1000, Civilization.Water, "DeckA");
        var c2 = CardFactory.Creature(1, 1000, Civilization.Water, "DeckB");
        var c3 = CardFactory.Creature(1, 1000, Civilization.Water, "DeckC");
        var c4 = CardFactory.Creature(1, 1000, Civilization.Water, "DeckD");
        h.P1.Deck.AddRange(new[] { c1, c2, c3, c4 });

        h.Game.ActivateTapAbilityScry(h.P1.BattleZone.IndexOf(garatyano));

        Assert.True(garatyano.IsTapped); // tapping pays the cost
        Assert.True(h.Game.IsScryWindowActive);
        Assert.Same(h.P1, h.Game.ScryOwner);
        Assert.Equal(new[] { c1, c2, c3 }, h.Game.ScryCards);
        Assert.Equal(4, h.P1.Deck.Count); // the deck is untouched until the order is submitted

        h.Game.SubmitScryOrder(new[] { c3, c1, c2 });

        Assert.False(h.Game.IsScryWindowActive);
        Assert.Empty(h.Game.ScryCards);
        Assert.Equal(new[] { c3, c1, c2, c4 }, h.P1.Deck); // top 3 reordered, rest unchanged
    }

    [Fact]
    public void Scry_RejectsAnyOrderThatIsNotAnExactPermutation()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var garatyano = h.PutCreature(h.P1, ScryCard(count: 3));
        h.P1.Deck.Clear();
        var c1 = CardFactory.Creature(1, 1000, Civilization.Water, "DeckA");
        var c2 = CardFactory.Creature(1, 1000, Civilization.Water, "DeckB");
        var c3 = CardFactory.Creature(1, 1000, Civilization.Water, "DeckC");
        var c4 = CardFactory.Creature(1, 1000, Civilization.Water, "DeckD");
        h.P1.Deck.AddRange(new[] { c1, c2, c3, c4 });

        h.Game.ActivateTapAbilityScry(h.P1.BattleZone.IndexOf(garatyano));

        // Too few, duplicates, and a foreign card are all rejected, and the window
        // stays open with the deck untouched so the player can try again.
        Assert.Throws<RuleViolationException>(() => h.Game.SubmitScryOrder(new[] { c1, c2 }));
        Assert.Throws<RuleViolationException>(() => h.Game.SubmitScryOrder(new[] { c1, c1, c1 }));
        Assert.Throws<RuleViolationException>(() => h.Game.SubmitScryOrder(new[] { c1, c2, c4 }));
        Assert.Throws<RuleViolationException>(() => h.Game.SubmitScryOrder(null!));

        Assert.True(h.Game.IsScryWindowActive);
        Assert.Equal(new[] { c1, c2, c3, c4 }, h.P1.Deck);
    }

    [Fact]
    public void Scry_NoWindow_SubmitThrows()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();

        var ex = Assert.Throws<RuleViolationException>(() => h.Game.SubmitScryOrder(Array.Empty<Card>()));
        Assert.Contains("deck-order", ex.Message);
    }

    [Fact]
    public void Scry_BlocksAllOtherActionsUntilSubmitted()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var garatyano = h.PutCreature(h.P1, ScryCard(count: 3));
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Fire, "Attacker"));
        var victim = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Nature, "Victim"), tapped: true);
        var mage = h.PutCreature(h.P1, CardFactory.TapCreature(1, 3000, Civilization.Water, "Mage",
            CardFactory.Eff(EffectId.Tap_Draw, value: 1)));
        var manaCard = h.PutInHand(h.P1, CardFactory.Spell(1));
        var creatureCard = h.PutInHand(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "SummonMe"));
        h.P1.Deck.Clear();
        for (var i = 0; i < 6; i++)
            h.P1.Deck.Add(CardFactory.Creature(1, 1000, Civilization.Water, $"Deck{i}"));

        h.Game.ActivateTapAbilityScry(h.P1.BattleZone.IndexOf(garatyano));

        // Every other action is refused while the decision is pending.
        Assert.False(h.Game.CanUseTapAbility(h.P1, h.P1.BattleZone.IndexOf(mage)));
        Assert.Throws<RuleViolationException>(() => h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(mage)));
        Assert.False(h.Game.CanUseTapAbility(h.P1, h.P1.BattleZone.IndexOf(attacker)));
        Assert.Throws<RuleViolationException>(() => h.Game.PlayManaToManaZone(h.P1.Hand.IndexOf(manaCard)));
        Assert.Throws<RuleViolationException>(() => h.Game.SummonCreature(h.P1.Hand.IndexOf(creatureCard)));
        Assert.Throws<RuleViolationException>(() => h.Game.AttackPlayer(h.P1.BattleZone.IndexOf(attacker)));
        Assert.Throws<RuleViolationException>(() => h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(victim)));
        Assert.Throws<RuleViolationException>(() => h.Game.EndMainPhase());

        Assert.True(h.Game.IsScryWindowActive);

        // The identity order resolves the window and the turn may continue normally.
        h.Game.SubmitScryOrder(h.Game.ScryCards.ToList());
        h.Game.EndMainPhase();
        h.Game.EndTurn();
    }

    [Fact]
    public void Scry_IdentityOrder_ResolvesDeterministically()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var garatyano = h.PutCreature(h.P1, ScryCard(count: 2));
        h.P1.Deck.Clear();
        var c1 = CardFactory.Creature(1, 1000, Civilization.Water, "DeckA");
        var c2 = CardFactory.Creature(1, 1000, Civilization.Water, "DeckB");
        h.P1.Deck.AddRange(new[] { c1, c2, c1, c2 });

        h.Game.ActivateTapAbilityScry(h.P1.BattleZone.IndexOf(garatyano));
        h.Game.SubmitScryOrder(h.Game.ScryCards.ToList());

        Assert.Equal(new[] { c1, c2, c1, c2 }, h.P1.Deck);
    }

    [Fact]
    public void Scry_GatedByDeckSize()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var garatyano = h.PutCreature(h.P1, ScryCard(count: 3));
        h.P1.Deck.Clear();
        h.P1.Deck.Add(CardFactory.Creature(1, 1000, Civilization.Water, "Only"));

        Assert.False(h.Game.CanUseTapAbility(h.P1, h.P1.BattleZone.IndexOf(garatyano)));
        var ex = Assert.Throws<RuleViolationException>(() => h.Game.ActivateTapAbilityScry(h.P1.BattleZone.IndexOf(garatyano)));
        Assert.Contains("not have enough cards", ex.Message);
        Assert.False(garatyano.IsTapped);
    }

    [Fact]
    public void Scry_RefusedWhileAScryWindowIsAlreadyOpen()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var a = h.PutCreature(h.P1, ScryCard(count: 2, civ: Civilization.Water));
        var b = h.PutCreature(h.P1, ScryCard(count: 2, civ: Civilization.Light));
        h.P1.Deck.Clear();
        for (var i = 0; i < 6; i++)
            h.P1.Deck.Add(CardFactory.Creature(1, 1000, Civilization.Water, $"Deck{i}"));

        h.Game.ActivateTapAbilityScry(h.P1.BattleZone.IndexOf(a));
        Assert.True(h.Game.IsScryWindowActive);

        // Even another scry creature is gated behind the pending decision.
        Assert.False(h.Game.CanUseTapAbility(h.P1, h.P1.BattleZone.IndexOf(b)));
        Assert.Throws<RuleViolationException>(() => h.Game.ActivateTapAbilityScry(h.P1.BattleZone.IndexOf(b)));
        Assert.False(b.IsTapped);
    }

    [Fact]
    public void Scry_GenericActivationPath_RefusesWithoutADeckOrder()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var garatyano = h.PutCreature(h.P1, ScryCard(count: 3));

        var ex = Assert.Throws<RuleViolationException>(() => h.Game.ActivateTapAbility(h.P1.BattleZone.IndexOf(garatyano)));
        Assert.Contains("deck-order", ex.Message);
        Assert.False(garatyano.IsTapped);
    }

    [Fact]
    public void Scry_WorksWithAVariablePeekCount()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var garatyano = h.PutCreature(h.P1, ScryCard(count: 5));
        h.P1.Deck.Clear();
        for (var i = 0; i < 7; i++)
            h.P1.Deck.Add(CardFactory.Creature(1, 1000, Civilization.Water, $"Deck{i}"));

        h.Game.ActivateTapAbilityScry(h.P1.BattleZone.IndexOf(garatyano));

        Assert.Equal(5, h.Game.ScryCards.Count);
        Assert.Equal(h.P1.Deck.Take(5), h.Game.ScryCards);

        var reversed = h.Game.ScryCards.Reverse().ToList();
        h.Game.SubmitScryOrder(reversed);
        Assert.Equal(reversed.Concat(h.P1.Deck.Skip(5)), h.P1.Deck);
    }

    // ---------------------------------------------------------------- AI policy

    [Fact]
    public void AiController_DeclinesShieldLook()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var adomis = h.PutCreature(h.P1, ShieldLookCard());
        var s1 = CardFactory.Creature(1, 1000, Civilization.Light, "S1");
        var s2 = CardFactory.Creature(1, 1000, Civilization.Light, "S2");
        h.SetShields(h.P1, s1, s2);

        new AiController(h.P1).PlayTurn(h.Game);

        // Peeking a shield buys nothing, so the AI passes over the ability: the
        // creature stays untapped and the shields stay face-down exactly as they were.
        Assert.False(adomis.IsTapped);
        Assert.Same(s1, h.P1.Shields[0]);
        Assert.Same(s2, h.P1.Shields[1]);
    }

    [Fact]
    public void AiController_DeclinesScry()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var garatyano = h.PutCreature(h.P1, ScryCard(count: 3));
        var deckTop = h.P1.Deck.Take(3).ToList();

        new AiController(h.P1).PlayTurn(h.Game);

        Assert.False(garatyano.IsTapped);
        Assert.False(h.Game.IsScryWindowActive);
        Assert.Equal(deckTop, h.P1.Deck.Take(3));
    }

    [Fact]
    public void AiController_ResolvesAnOpenScryWindowInDrawOrder()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var garatyano = h.PutCreature(h.P1, ScryCard(count: 3));
        h.P1.Deck.Clear();
        var c1 = CardFactory.Creature(1, 1000, Civilization.Water, "DeckA");
        var c2 = CardFactory.Creature(1, 1000, Civilization.Water, "DeckB");
        var c3 = CardFactory.Creature(1, 1000, Civilization.Water, "DeckC");
        h.P1.Deck.AddRange(new[] { c1, c2, c3, c1, c2, c3 });

        h.Game.ActivateTapAbilityScry(h.P1.BattleZone.IndexOf(garatyano));
        Assert.True(h.Game.IsScryWindowActive);

        new AiController(h.P1).PlayTurn(h.Game);

        Assert.False(h.Game.IsScryWindowActive);
        Assert.Equal(new[] { c1, c2, c3, c1, c2, c3 }, h.P1.Deck); // identity order
    }

    // ------------------------------------------------------------- networked state

    [Fact]
    public void DuelGameState_AnnotatesTheShieldLookAndScryDecisionKinds()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var adomis = h.PutCreature(h.P1, ShieldLookCard());
        h.SetShields(h.P1, CardFactory.Creature(1, 1000, Civilization.Light, "S1"), CardFactory.Creature(1, 1000, Civilization.Light, "S2"));
        var garatyano = h.PutCreature(h.P1, ScryCard(count: 3));
        var draw = h.PutCreature(h.P1, CardFactory.TapCreature(1, 3000, Civilization.Water, "Mage",
            CardFactory.Eff(EffectId.Tap_Draw, value: 1)));

        var state = DuelGameState.From(h.Game, "AAAAAA", DuelSide.Player1);

        var own = state.Players.Single(p => p.Side == DuelSide.Player1).BattleZone;
        Assert.Equal("shield", own.Single(c => c.CardId == adomis.Card.Id).TapDecisionKind);
        Assert.Equal("scry", own.Single(c => c.CardId == garatyano.Card.Id).TapDecisionKind);
        Assert.Null(own.Single(c => c.CardId == draw.Card.Id).TapDecisionKind);
    }

    [Fact]
    public void DuelGameState_ScryWindow_IsVisibleOnlyToItsOwner()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var garatyano = h.PutCreature(h.P1, ScryCard(count: 3));
        h.P1.Deck.Clear();
        for (var i = 0; i < 6; i++)
            h.P1.Deck.Add(CardFactory.Creature(1, 1000, Civilization.Water, $"Deck{i}"));
        h.Game.ActivateTapAbilityScry(h.P1.BattleZone.IndexOf(garatyano));

        var ownerView = DuelGameState.From(h.Game, "AAAAAA", DuelSide.Player1);
        Assert.True(ownerView.ScryWindowActive);
        Assert.Equal(DuelSide.Player1, ownerView.ScryOwnerSide);
        Assert.Equal(3, ownerView.ScryCards.Count);
        Assert.Equal(h.P1.Deck.Take(3).Select(c => c.Id), ownerView.ScryCards.Select(c => c.CardId));
        Assert.All(ownerView.ScryCards, c => Assert.StartsWith("Scry:", c.InstanceId));

        // The opponent sees the window exists (their UI must not swing freely) but
        // never its cards.
        var foeView = DuelGameState.From(h.Game, "AAAAAA", DuelSide.Player2);
        Assert.True(foeView.ScryWindowActive);
        Assert.Equal(DuelSide.Player1, foeView.ScryOwnerSide);
        Assert.Empty(foeView.ScryCards);

        h.Game.SubmitScryOrder(h.Game.ScryCards.ToList());
        var resolved = DuelGameState.From(h.Game, "AAAAAA", DuelSide.Player1);
        Assert.False(resolved.ScryWindowActive);
        Assert.Null(resolved.ScryOwnerSide);
        Assert.Empty(resolved.ScryCards);
    }

    [Fact]
    public void CardState_FromCard_SerializesRawCardsForPeekedShieldsAndScry()
    {
        var card = CardFactory.TapCreature(3, 2000, Civilization.Light, "Plain", null);

        var state = CardState.FromCard(card, "Shield:0");

        Assert.Equal("Shield:0", state.InstanceId);
        Assert.Equal(card.Id, state.CardId);
        Assert.Equal("Plain", state.Name);
        Assert.Equal("Light", state.Civilization);
        Assert.Equal(card.Power, state.Power);
        Assert.Equal(card.ManaCost, state.ManaCost);
    }

    // ------------------------------------------------------------ scry reordering tokens

    [Fact]
    public void ScryToken_Mapping_IsPositionalAndHandlesDuplicateCardIds()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var garatyano = h.PutCreature(h.P1, ScryCard(count: 3));
        var dup = CardFactory.Creature(1, 1000, Civilization.Water, "Twins");
        h.P1.Deck.Clear();
        h.P1.Deck.Add(dup);      // index 0
        h.P1.Deck.Add(dup);      // index 1, same catalog id
        h.P1.Deck.Add(CardFactory.Creature(1, 1000, Civilization.Water, "Other"));
        h.Game.ActivateTapAbilityScry(h.P1.BattleZone.IndexOf(garatyano));

        var state = DuelGameState.From(h.Game, "AAAAAA", DuelSide.Player1);
        Assert.Equal(3, state.ScryCards.Count);
        Assert.Equal(new[] { "Scry:0", "Scry:1", "Scry:2" }, state.ScryCards.Select(c => c.InstanceId));
    }
}