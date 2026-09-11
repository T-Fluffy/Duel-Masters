using System.Linq;
using DuelMasters.Domain;
using Xunit;

namespace DuelMasters.Domain.Tests;

/// <summary>
/// Coverage for the D4 promotion set: on-play, attack-trigger, on-destroyed and
/// spell effect ids mapped from DM-04..DM-09 card text. The engine approximates
/// opponent choices deterministically (weakest creature, most-recent card, first
/// deck match) so every scenario below asserts a concrete outcome.
/// </summary>
public class PromotedEffectTests
{
    private static void Mana(GameHarness h, Player owner, Civilization civ, int count = 1)
    {
        for (var i = 0; i < count; i++)
            h.PutMana(owner, CardFactory.Creature(i, 1000, civ, $"Mana-{i}"));
    }

    private static void Grave(GameHarness h, Player owner, params Card[] cards)
    {
        foreach (var c in cards)
            owner.Graveyard.Add(new CardInstance(c, owner) { Zone = Zone.Graveyard });
    }

    // ---------------------------------------------------------------- on play

    [Fact]
    public void OnPlay_DestroyAny_DestroysTargetOpponentCreature()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Fire);
        var cre = CardFactory.Creature(1, 2000, Civilization.Fire, "Tyrant",
            CardFactory.Eff(EffectId.OnPlay_DestroyAny, EffectTargetScope.OpponentCreature));
        h.PutInHand(h.P1, cre);
        var victim = h.PutCreature(h.P2, CardFactory.Creature(1, 9000, Civilization.Darkness, "Bulwark"));

        var summoned = h.Game.SummonCreature(0, h.P2, h.P2.BattleZone.IndexOf(victim));

        Assert.Contains(summoned, h.P1.BattleZone);
        Assert.DoesNotContain(victim, h.P2.BattleZone);
        Assert.Contains(victim, h.P2.Graveyard);
    }

    [Fact]
    public void OnPlay_DestroyAny_WithoutTarget_FizzlesHarmlessly()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Fire);
        var cre = CardFactory.Creature(1, 2000, Civilization.Fire, "Tyrant",
            CardFactory.Eff(EffectId.OnPlay_DestroyAny, EffectTargetScope.OpponentCreature));
        h.PutInHand(h.P1, cre);

        var summoned = h.Game.SummonCreature(0);

        Assert.Contains(summoned, h.P1.BattleZone);
    }

    [Fact]
    public void OnPlay_DestroyOwnCreature_DestroysOwnCreatureWithinPowerCap()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Fire);
        var cre = CardFactory.Creature(1, 2000, Civilization.Fire, "Cultist",
            CardFactory.Eff(EffectId.OnPlay_DestroyOwnCreature, EffectTargetScope.OwnCreature, value: 3000));
        h.PutInHand(h.P1, cre);
        var sacrifice = h.PutCreature(h.P1, CardFactory.Creature(1, 2000, Civilization.Fire, "Offering"));

        h.Game.SummonCreature(0, h.P1, h.P1.BattleZone.IndexOf(sacrifice));

        Assert.DoesNotContain(sacrifice, h.P1.BattleZone);
        Assert.Contains(sacrifice, h.P1.Graveyard);
    }

    [Fact]
    public void OnPlay_DestroyOwnCreature_RejectsOwnCreatureAbovePowerCap()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Fire);
        var cre = CardFactory.Creature(1, 2000, Civilization.Fire, "Cultist",
            CardFactory.Eff(EffectId.OnPlay_DestroyOwnCreature, EffectTargetScope.OwnCreature, value: 3000));
        h.PutInHand(h.P1, cre);
        var tough = h.PutCreature(h.P1, CardFactory.Creature(1, 9000, Civilization.Fire, "Champion"));

        Assert.Throws<RuleViolationException>(
            () => h.Game.SummonCreature(0, h.P1, h.P1.BattleZone.IndexOf(tough)));

        Assert.Contains(tough, h.P1.BattleZone);
        Assert.Single(h.P1.Hand); // the creature stayed in hand
        Assert.All(h.P1.ManaZone, m => Assert.False(m.IsTapped)); // mana untouched
    }

    [Fact]
    public void OnPlay_ReturnFromGraveyard_ReturnsMostRecentFilteredCard()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Water);
        Grave(h, h.P1,
            CardFactory.Spell(1, Civilization.Water, "Buried Spell"),
            CardFactory.Creature(1, 1000, Civilization.Fire, "Buried Champ"));
        var cre = CardFactory.Creature(1, 2000, Civilization.Water, "Reanimator",
            CardFactory.Eff(EffectId.OnPlay_ReturnFromGraveyard, value: 1, data: "creature"));
        h.PutInHand(h.P1, cre);

        h.Game.SummonCreature(0);

        Assert.Single(h.P1.Hand);
        Assert.Equal("Buried Champ", h.P1.Hand[0].Card.Name);
        Assert.Single(h.P1.Graveyard);
    }

    [Fact]
    public void OnPlay_ReturnFromMana_MovesTopManaToHand()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Water, 2);
        var cre = CardFactory.Creature(1, 2000, Civilization.Water, "Aqua Sage",
            CardFactory.Eff(EffectId.OnPlay_ReturnFromMana, value: 1));
        h.PutInHand(h.P1, cre);

        h.Game.SummonCreature(0);

        Assert.Single(h.P1.ManaZone);
        Assert.Single(h.P1.Hand);
    }

    [Fact]
    public void OnPlay_SearchDeck_TakesMatchingCardIntoHandAndShuffles()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Nature);
        var hunted = CardFactory.Creature(1, 1000, Civilization.Fire, "Prey");
        h.P1.Deck.Insert(0, hunted);
        var cre = CardFactory.Creature(1, 2000, Civilization.Nature, "Scout",
            CardFactory.Eff(EffectId.OnPlay_SearchDeck, value: 1, data: "Prey"));
        h.PutInHand(h.P1, cre);

        var before = h.P1.Deck.Count;
        h.Game.SummonCreature(0);

        Assert.Equal(before - 1, h.P1.Deck.Count);
        Assert.Contains(hunted, h.P1.Hand.Select(c => c.Card));
    }

    [Fact]
    public void OnPlay_DiscardOpponentRandom_MakesOpponentDiscardOne()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Darkness);
        h.PutInHand(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "H1"));
        h.PutInHand(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "H2"));
        var cre = CardFactory.Creature(1, 2000, Civilization.Darkness, "Sinister",
            CardFactory.Eff(EffectId.OnPlay_DiscardOpponentRandom, value: 1));
        h.PutInHand(h.P1, cre);

        h.Game.SummonCreature(0);

        Assert.Equal(1, h.P2.Hand.Count);
        Assert.Single(h.P2.Graveyard);
    }

    [Fact]
    public void OnPlay_OpponentSacrifice_OpponentDestroysWeakestCreature()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Darkness);
        var weak = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Darkness, "Mook"));
        var strong = h.PutCreature(h.P2, CardFactory.Creature(1, 7000, Civilization.Darkness, "Boss"));
        var cre = CardFactory.Creature(1, 2000, Civilization.Darkness, "Demon",
            CardFactory.Eff(EffectId.OnPlay_OpponentSacrifice));
        h.PutInHand(h.P1, cre);

        h.Game.SummonCreature(0);

        Assert.DoesNotContain(weak, h.P2.BattleZone);
        Assert.Contains(strong, h.P2.BattleZone);
    }

    [Fact]
    public void OnPlay_FromGraveyardToMana_MovesFilteredCardToMana()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Nature);
        Grave(h, h.P1,
            CardFactory.Creature(1, 1000, Civilization.Fire, "Old Bone"),
            CardFactory.Creature(1, 1000, Civilization.Nature, "Fertile Soil"));
        var cre = CardFactory.Creature(1, 2000, Civilization.Nature, "Bloom",
            CardFactory.Eff(EffectId.OnPlay_FromGraveyardToMana, value: 1, data: "Fertile Soil"));
        h.PutInHand(h.P1, cre);

        h.Game.SummonCreature(0);

        Assert.Contains("Fertile Soil", h.P1.ManaZone.Select(c => c.Card.Name));
        Assert.Single(h.P1.Graveyard);
    }

    [Fact]
    public void OnPlay_ManaToGrave_MovesTopManaToGraveyard()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Fire);
        var cre = CardFactory.Creature(1, 2000, Civilization.Fire, "Ruinator",
            CardFactory.Eff(EffectId.OnPlay_ManaToGrave));
        h.PutInHand(h.P1, cre);

        h.Game.SummonCreature(0);

        Assert.Empty(h.P1.ManaZone);
        Assert.Single(h.P1.Graveyard);
    }

    // ------------------------------------------------------------ attack triggers

    [Fact]
    public void AttackTrigger_Draw_DrawsWhenAttackingCreature()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Fire, "Raider",
            CardFactory.Eff(EffectId.AttackTrigger_Draw, value: 1)));
        var target = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "Drone"), tapped: true);

        var before = h.P1.Deck.Count;
        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(target));

        Assert.Equal(before - 1, h.P1.Deck.Count);
        Assert.Single(h.P1.Hand);
    }

    [Fact]
    public void AttackTrigger_DiscardOpponentRandom_DiscardsOnAttack()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Darkness, "Wraith",
            CardFactory.Eff(EffectId.AttackTrigger_DiscardOpponentRandom, value: 1)));
        var target = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "Drone"), tapped: true);
        h.PutInHand(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "C1"));
        h.PutInHand(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "C2"));

        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(target));

        Assert.Equal(1, h.P2.Hand.Count);
        Assert.Equal(2, h.P2.Graveyard.Count); // discarded card + the drone slain in battle
    }

    [Fact]
    public void AttackTrigger_ReturnFromGraveyard_ReturnsFilteredCardOnAttack()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Grave(h, h.P1,
            CardFactory.Spell(1, Civilization.Water, "Lost Spell"),
            CardFactory.Creature(1, 1000, Civilization.Fire, "Lost Champ"));
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Water, "Aqua Ghost",
            CardFactory.Eff(EffectId.AttackTrigger_ReturnFromGraveyard, value: 1, data: "creature")));
        var target = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "Drone"), tapped: true);

        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(target));

        Assert.Single(h.P1.Hand);
        Assert.Equal("Lost Champ", h.P1.Hand[0].Card.Name);
        Assert.Single(h.P1.Graveyard); // the unmatched Lost Spell stayed behind
    }

    [Fact]
    public void AttackTrigger_ChargeMana_PutsTopOfDeckIntoManaOnAttack()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Nature, "Charger",
            CardFactory.Eff(EffectId.AttackTrigger_ChargeMana)));
        var target = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "Drone"), tapped: true);

        var before = h.P1.Deck.Count;
        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(target));

        Assert.Equal(before - 1, h.P1.Deck.Count);
        Assert.Single(h.P1.ManaZone);
    }

    [Fact]
    public void AttackTrigger_TapCreature_TapsWeakestCreatureOfMatchingCivilization()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Fire, "Tapper",
            CardFactory.Eff(EffectId.AttackTrigger_TapCreature, data: "fire or nature")));
        var fire = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "Torch"));
        var nature = h.PutCreature(h.P2, CardFactory.Creature(1, 7000, Civilization.Nature, "Tree"), tapped: true);

        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(nature));

        Assert.True(fire.IsTapped);
        Assert.Contains(fire, h.P2.BattleZone);
    }

    // ------------------------------------------------------------ on destroyed

    [Fact]
    public void OnDestroyed_OpponentDiscardRandom_OpponentDiscardsRandomCard()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Fire, "Huntsman"));
        var victim = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Darkness, "Doomed",
            CardFactory.Eff(EffectId.OnDestroyed_OpponentDiscardRandom, value: 1)), tapped: true);
        h.PutInHand(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "C1"));
        h.PutInHand(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "C2"));

        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(victim));

        Assert.Equal(1, h.P1.Hand.Count);
        Assert.Single(h.P1.Graveyard);
        Assert.Contains(victim, h.P2.Graveyard);
    }

    [Fact]
    public void OnDestroyed_DiscardHand_DiscardsBothPlayersHands()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Fire, "Huntsman"));
        var victim = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Darkness, "Doomed",
            CardFactory.Eff(EffectId.OnDestroyed_DiscardHand)), tapped: true);
        h.PutInHand(h.P1, CardFactory.Creature(1, 1000, Civilization.Fire, "C1"));
        h.PutInHand(h.P2, CardFactory.Creature(1, 1000, Civilization.Fire, "C2"));

        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(victim));

        Assert.Empty(h.P1.Hand);
        Assert.Empty(h.P2.Hand);
        Assert.Single(h.P1.Graveyard);
        Assert.Equal(2, h.P2.Graveyard.Count);
    }

    [Fact]
    public void OnDestroyed_DestroyMana_BothPlayersLoseAMana()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Fire, 2);
        Mana(h, h.P2, Civilization.Fire, 2);
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Fire, "Huntsman"));
        var victim = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Darkness, "Doomed",
            CardFactory.Eff(EffectId.OnDestroyed_DestroyMana, value: 1)), tapped: true);

        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(victim));

        Assert.Equal(1, h.P1.ManaZone.Count);
        Assert.Equal(1, h.P2.ManaZone.Count);
        Assert.Single(h.P1.Graveyard);
        Assert.Equal(2, h.P2.Graveyard.Count);
    }

    [Fact]
    public void OnDestroyed_DestroyAllPowerAtMost_DestroysWeakCreaturesEverywhere()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Fire, "Warlord"));
        var victim = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Darkness, "Doomed",
            CardFactory.Eff(EffectId.OnDestroyed_DestroyAllPowerAtMost, value: 3000)), tapped: true);
        var weak = h.PutCreature(h.P1, CardFactory.Creature(1, 2000, Civilization.Fire, "Mook"));
        var standing = h.PutCreature(h.P2, CardFactory.Creature(1, 5000, Civilization.Fire, "Standing"));

        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(victim));

        Assert.Contains(weak, h.P1.Graveyard);
        Assert.Contains(standing, h.P2.BattleZone);
        Assert.Contains(attacker, h.P1.BattleZone); // 8000 over the cap survived
    }

    [Fact]
    public void OnDestroyed_ReturnFromGraveyard_DyingCreatureReturnsToHand()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Fire, "Huntsman"));
        var victim = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Darkness, "Returner",
            CardFactory.Eff(EffectId.OnDestroyed_ReturnFromGraveyard, value: 1)), tapped: true);

        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(victim));

        Assert.Contains(victim, h.P2.Hand);
        Assert.DoesNotContain(victim, h.P2.Graveyard);
    }

    [Fact]
    public void OnDestroyed_ShieldToHand_PutsAShieldIntoHand()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Fire, "Huntsman"));
        h.SetShields(h.P2, CardFactory.Creature(0, 1000, Civilization.Fire, "ShieldA"));
        var victim = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Darkness, "Shielder",
            CardFactory.Eff(EffectId.OnDestroyed_ShieldToHand)), tapped: true);

        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(victim));

        Assert.Equal(0, h.P2.Shields.Count);
        Assert.Single(h.P2.Hand);
    }

    [Fact]
    public void OnDestroyed_ShieldToGrave_PutsAShieldIntoGraveyard()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        var attacker = h.PutCreature(h.P1, CardFactory.Creature(1, 8000, Civilization.Fire, "Huntsman"));
        h.SetShields(h.P2, CardFactory.Creature(0, 1000, Civilization.Fire, "ShieldA"));
        var victim = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Darkness, "Shielder",
            CardFactory.Eff(EffectId.OnDestroyed_ShieldToGrave)), tapped: true);

        h.Game.AttackCreature(h.P1.BattleZone.IndexOf(attacker), h.P2.BattleZone.IndexOf(victim));

        Assert.Equal(0, h.P2.Shields.Count);
        Assert.Equal(2, h.P2.Graveyard.Count);
        Assert.Empty(h.P2.Hand);
    }

    // ------------------------------------------------------------------- spells

    [Fact]
    public void DestroyAnySpell_DestroysCreatureOfAnyPower()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Darkness, 2);
        var spell = CardFactory.Spell(2, Civilization.Darkness, "Annihilate",
            CardFactory.Eff(EffectId.Spell_DestroyAny, EffectTargetScope.AnyCreature));
        h.PutInHand(h.P1, spell);
        var victim = h.PutCreature(h.P2, CardFactory.Creature(1, 9000, Civilization.Darkness, "Colossus"));

        h.Game.CastSpell(0, h.P2, h.P2.BattleZone.IndexOf(victim));

        Assert.DoesNotContain(victim, h.P2.BattleZone);
        Assert.Contains(victim, h.P2.Graveyard);
    }

    [Fact]
    public void OpponentSacrificeSpell_OpponentDestroysWeakestCreature()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Darkness, 2);
        var spell = CardFactory.Spell(2, Civilization.Darkness, "Edict",
            CardFactory.Eff(EffectId.Spell_OpponentSacrifice));
        h.PutInHand(h.P1, spell);
        var weak = h.PutCreature(h.P2, CardFactory.Creature(1, 1000, Civilization.Darkness, "Mook"));
        var strong = h.PutCreature(h.P2, CardFactory.Creature(1, 7000, Civilization.Darkness, "Boss"));

        h.Game.CastSpell(0);

        Assert.DoesNotContain(weak, h.P2.BattleZone);
        Assert.Contains(strong, h.P2.BattleZone);
    }

    [Fact]
    public void SearchToHandSpell_SearchesDeckForFilteredCard()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Water, 2);
        var spell = CardFactory.Spell(2, Civilization.Water, "Quest",
            CardFactory.Eff(EffectId.Spell_SearchToHand, data: "Champion"));
        h.PutInHand(h.P1, spell);
        var hunted = CardFactory.Creature(1, 1000, Civilization.Fire, "Old Champion");
        h.P1.Deck.Insert(0, hunted);

        var before = h.P1.Deck.Count;
        h.Game.CastSpell(0);

        Assert.Equal(before - 1, h.P1.Deck.Count);
        Assert.Contains(hunted, h.P1.Hand.Select(c => c.Card));
    }

    [Fact]
    public void SearchToManaSpell_PutsFoundCardIntoManaZone()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Water, 2);
        var spell = CardFactory.Spell(2, Civilization.Water, "Excavate",
            CardFactory.Eff(EffectId.Spell_SearchToMana, data: "Champion"));
        h.PutInHand(h.P1, spell);
        var hunted = CardFactory.Creature(1, 1000, Civilization.Fire, "Old Champion");
        h.P1.Deck.Insert(0, hunted);

        var before = h.P1.Deck.Count;
        h.Game.CastSpell(0);

        Assert.Equal(before - 1, h.P1.Deck.Count);
        Assert.Contains(hunted, h.P1.ManaZone.Select(c => c.Card));
    }

    [Fact]
    public void ReturnFromGraveyardSpell_ReturnsFilteredCardsToHand()
    {
        var h = GameHarness.AtMainPhase();
        h.ResetBoard();
        Mana(h, h.P1, Civilization.Water, 2);
        var spell = CardFactory.Spell(2, Civilization.Water, "Revive",
            CardFactory.Eff(EffectId.Spell_ReturnFromGraveyard, value: 1, data: "creature"));
        h.PutInHand(h.P1, spell);
        Grave(h, h.P1,
            CardFactory.Spell(1, Civilization.Water, "Dead Spell"),
            CardFactory.Creature(1, 1000, Civilization.Fire, "Dead Champ"));

        h.Game.CastSpell(0);

        Assert.Single(h.P1.Hand);
        Assert.Equal("Dead Champ", h.P1.Hand[0].Card.Name);
        Assert.Equal(2, h.P1.Graveyard.Count); // unmatched Dead Spell + the cast Revive
    }
}