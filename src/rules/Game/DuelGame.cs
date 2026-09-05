using System;
using System.Collections.Generic;
using System.Linq;

namespace DuelMasters.Domain;

/// <summary>
/// Turn-by-turn rules engine for a two-player Duel Masters game.
///
/// Pure and engine-independent: the Godot client, backend, AI and tests all share
/// this logic. It enforces the Phase 2 flow - Untap, Draw, Main (mana -> summon ->
/// attacks), End - with shield breaking and the shield-trigger interrupt window.
/// </summary>
public sealed class DuelGame
{
    private int _activeIndex;
    private bool _manaChargedThisTurn;
    private bool _hasAttackedThisTurn;

    public DuelGame(Player player1, Player player2, Random? rng = null)
    {
        Player1 = player1 ?? throw new ArgumentNullException(nameof(player1));
        Player2 = player2 ?? throw new ArgumentNullException(nameof(player2));
        Rng = rng ?? new Random();
    }

    public Player Player1 { get; }
    public Player Player2 { get; }
    private Random Rng { get; }

    public GamePhase Phase { get; private set; } = GamePhase.Untap;
    public int TurnNumber { get; private set; } = 1;

    /// <summary>True if the active player has already charged a mana card this turn.</summary>
    public bool ManaChargedThisTurn => _manaChargedThisTurn;

    /// <summary>True if the active player has already attacked this turn (attack lock).</summary>
    public bool HasAttackedThisTurn => _hasAttackedThisTurn;

    /// <summary>The player whose turn it currently is.</summary>
    public Player ActivePlayer => _activeIndex == 0 ? Player1 : Player2;

    /// <summary>The opponent of the active player.</summary>
    public Player Opponent => _activeIndex == 0 ? Player2 : Player1;

    /// <summary>Set once the game is actually won (0 shields, or deck out).</summary>
    public Player? Winner { get; private set; }

    public bool IsGameOver => Winner is not null;

    // ------------------------------------------------------------------ setup

    /// <summary>
    /// Shuffle decks, place 5 face-down shields, draw the opening hand.
    /// With <paramref name="shuffle"/> set to <c>false</c> the decks keep their
    /// caller-supplied order (index 0 remains the top), which makes turn-by-turn
    /// sequences deterministic and easy to assert in tests.
    /// </summary>
    /// <exception cref="RuleViolationException">A deck has fewer than 10 cards.</exception>
    public void StartGame(bool shuffle = true)
    {
        foreach (var p in new[] { Player1, Player2 })
        {
            if (p.Deck.Count < 10)
                throw new RuleViolationException(
                    $"'{p.Name}' needs at least 10 cards to start (5 shields + 5 opening hand), but only has {p.Deck.Count}.");

            if (shuffle)
            {
                var shuffled = p.Deck.OrderBy(_ => Rng.Next()).ToList();
                p.Deck.Clear();
                p.Deck.AddRange(shuffled);
            }

            // The top 5 cards of the deck become the face-down shields.
            for (var i = 0; i < 5 && p.Deck.Count > 0; i++)
            {
                p.Shields.Add(p.Deck[0]);
                p.Deck.RemoveAt(0);
            }

            DrawToHand(p, 5);
        }

        _activeIndex = 0;
        TurnNumber = 1;
        Phase = GamePhase.Untap;
        Winner = null;
    }

    // ------------------------------------------------------------------ phases

    /// <summary>Begin the active player's turn: untap, reset per-turn flags, enter Draw.</summary>
    public void StartTurn()
    {
        EnsureNotOver();
        var active = ActivePlayer;

        foreach (var c in active.ManaZone) c.IsTapped = false;
        foreach (var c in active.BattleZone) c.IsTapped = false;
        // Creatures become ready at the start of their owner's turn.
        foreach (var c in active.BattleZone) c.IsSummoningSick = false;
        _manaChargedThisTurn = false;
        _hasAttackedThisTurn = false;

        Phase = GamePhase.Draw;
    }

    /// <summary>The active player draws one card, then proceeds to the Main phase.</summary>
    public void Draw(int amount = 1)
    {
        EnsureTurnPhase(GamePhase.Draw);
        DrawToHand(ActivePlayer, amount);
        CheckDeckOut(ActivePlayer);
        Phase = GamePhase.Main;
    }

    /// <summary>End the Main phase, moving to the End phase before the turn ends.</summary>
    public void EndMainPhase()
    {
        EnsureTurnPhase(GamePhase.Main);
        Phase = GamePhase.End;
    }

    /// <summary>End the active player's turn and advance to the opponent.</summary>
    public void EndTurn()
    {
        EnsureTurnPhase(GamePhase.End);
        _activeIndex = 1 - _activeIndex;
        TurnNumber++;
        Phase = GamePhase.Untap;
    }

    // -------------------------------------------------- main phase actions

    /// <summary>
    /// Deposit one hand card into the mana zone (tapped). A player may charge at
    /// most one card per turn by default; extra charges only come from card effects
    /// (Mana Acceleration) during a later phase.
    /// </summary>
    public void PlayManaToManaZone(int handIndex)
    {
        EnsureTurnPhase(GamePhase.Main);
        if (_manaChargedThisTurn)
            throw new RuleViolationException("You may only charge one mana card per turn.");
        RequireHandCard(ActivePlayer, handIndex);
        var card = TakeFromHand(ActivePlayer.Hand, handIndex);
        card.IsTapped = true;
        card.Zone = Zone.ManaZone;
        ActivePlayer.ManaZone.Add(card);
        _manaChargedThisTurn = true;
    }

    /// <summary>True if the player can tap enough untapped mana to play the card.</summary>
    public bool CanAfford(Player player, Card card)
    {
        return player.ManaZone.Count(m => !m.IsTapped) >= card.ManaCost;
    }

    /// <summary>
    /// True if <see cref="PayManaFor"/> will succeed for this card: enough untapped
    /// mana AND at least one untapped mana of the card's own civilization.
    /// </summary>
    public bool CanPlay(Player player, Card card)
    {
        var available = player.ManaZone.Where(m => !m.IsTapped).ToList();
        if (available.Count < card.ManaCost)
            return false;
        return available.Any(m => m.Card.Civilization == card.Civilization);
    }

    /// <summary>
    /// Summon a creature from the active player's hand into the battle zone, paying
    /// mana. It is summoning-sick (can't attack) unless it has Speed Attacker.
    /// </summary>
    public CardInstance SummonCreature(int handIndex)
    {
        EnsureTurnPhase(GamePhase.Main);
        if (_hasAttackedThisTurn)
            throw new RuleViolationException("You cannot summon a creature after a creature has attacked.");
        var active = ActivePlayer;
        RequireHandCard(active, handIndex);
        var card = active.Hand[handIndex];
        if (!card.Card.IsCreature)
            throw new RuleViolationException($"'{card.Card.Name}' is not a creature.");

        PayManaFor(active, card.Card);
        var instance = active.Hand[handIndex];
        instance.Zone = Zone.BattleZone;
        instance.IsSummoningSick = !card.Card.HasKeyword(Keyword.SpeedAttacker);
        active.Hand.RemoveAt(handIndex);
        active.BattleZone.Add(instance);
        return instance;
    }

    /// <summary>
    /// Cast a spell from the active player's hand (paid and then sent to the
    /// graveyard). This offline milestone resolves no named effects; that is the
    /// seam where scriptEffectId handlers plug in during a later phase.
    /// </summary>
    public CardInstance CastSpell(int handIndex)
    {
        EnsureTurnPhase(GamePhase.Main);
        if (_hasAttackedThisTurn)
            throw new RuleViolationException("You cannot cast a spell after a creature has attacked.");
        var active = ActivePlayer;
        RequireHandCard(active, handIndex);
        var card = active.Hand[handIndex];
        if (card.Card.CardType != CardType.Spell)
            throw new RuleViolationException($"'{card.Card.Name}' is not a spell.");

        PayManaFor(active, card.Card);
        var instance = active.Hand[handIndex];
        instance.Zone = Zone.Graveyard;
        active.Hand.RemoveAt(handIndex);
        active.Graveyard.Add(instance);
        return instance;
    }

    // ------------------------------------------------------------ combat

    /// <summary>
    /// The active player's creature at <paramref name="attackerIndex"/> attacks the
    /// defending player directly, breaking shields (or winning if no shields remain).
    ///
    /// If <paramref name="blockerOwner"/> and <paramref name="blockerIndex"/> are
    /// provided, the defender chooses an untapped creature with the Blocker keyword
    /// to intercept the attack; a battle happens instead of shields breaking. The
    /// attack is on the player, so a blocker may also be a summoning-sick creature.
    /// </summary>
    /// <exception cref="RuleViolationException">
    /// Blocking was requested but the blocker is not an untapped creature with the
    /// Blocker keyword, or belongs to a player other than the defender.
    /// </exception>
    public void AttackPlayer(int attackerIndex, Player? blockerOwner = null, int? blockerIndex = null)
    {
        EnsureMain();
        var active = ActivePlayer;
        var defender = Opponent;
        var attacker = RequireReadyAttacker(active, attackerIndex);

        attacker.IsTapped = true;
        _hasAttackedThisTurn = true;

        if (blockerOwner is not null && blockerIndex is int bIdx)
        {
            if (!ReferenceEquals(blockerOwner, defender))
                throw new RuleViolationException("Only the defending player may block this attack.");
            var blocker = RequireReadyBlocker(defender, bIdx);
            blocker.IsTapped = true;
            Battle(attacker, blocker);
            return;
        }

        if (defender.ShieldCount == 0)
        {
            Winner = active;
            return;
        }

        BreakShields(defender, attacker.Card.BreakerCount);
    }

    /// <summary>
    /// The active player's creature at <paramref name="attackerIndex"/> attacks a
    /// specific creature in the defender's battle zone directly. Under normal rules
    /// only a tapped enemy creature may be attacked this way. Higher power wins;
    /// equal power destroys both.
    /// </summary>
    public void AttackCreature(int attackerIndex, int targetIndex)
    {
        EnsureMain();
        var active = ActivePlayer;
        var defender = Opponent;
        var attacker = RequireReadyAttacker(active, attackerIndex);

        if (targetIndex < 0 || targetIndex >= defender.BattleZone.Count)
            throw new RuleViolationException("The target index is out of range of the defender's battle zone.");
        var target = defender.BattleZone[targetIndex];
        if (!target.Card.IsCreature)
            throw new RuleViolationException($"'{target.Card.Name}' is not a creature and cannot be attacked.");
        if (!target.IsTapped)
            throw new RuleViolationException("Under normal rules you may only attack a tapped creature.");

        attacker.IsTapped = true;
        _hasAttackedThisTurn = true;

        Battle(attacker, target);
    }

    /// <summary>
    /// Resolve a battle between two creatures: the higher power survives and the
    /// loser is sent to its owner's graveyard; equal power destroys both. The
    /// attacking creature is tapped, and a blocker used to intercept is tapped.
    /// </summary>
    private void Battle(CardInstance attacker, CardInstance defender)
    {
        var aPower = attacker.Card.Power;
        var dPower = defender.Card.Power;
        if (aPower > dPower)
            DestroyCreature(defender);
        else if (dPower > aPower)
            DestroyCreature(attacker);
        else
        {
            DestroyCreature(defender);
            DestroyCreature(attacker);
        }
    }

    // ------------------------------------------------------------ shields

    /// <summary>Remove the defender's top <paramref name="count"/> shields (returns them).</summary>
    private List<Card> BreakShields(Player defender, int count)
    {
        var broken = new List<Card>();
        for (var i = 0; i < count && defender.Shields.Count > 0; i++)
        {
            var shield = defender.Shields[0];
            defender.Shields.RemoveAt(0);
            broken.Add(shield);
        }
        ResolveShieldTriggers(defender, broken);
        return broken;
    }

    /// <summary>
    /// Every broken shield is added to the defender's hand. If it carries
    /// ShieldTrigger, its owner may instead play it for free immediately (the
    /// interrupt window) - modelled in this milestone by simply keeping it in hand
    /// for the player to play at no cost; executing the named effect is a later phase.
    /// </summary>
    private void ResolveShieldTriggers(Player defender, List<Card> broken)
    {
        foreach (var shield in broken)
            defender.Hand.Add(new CardInstance(shield, defender) { Zone = Zone.Hand });
    }

    // ------------------------------------------------------------ helpers

    private CardInstance RequireReadyAttacker(Player active, int index)
    {
        if (index < 0 || index >= active.BattleZone.Count)
            throw new RuleViolationException("The attacker index is out of range of the battle zone.");
        var attacker = active.BattleZone[index];
        if (!attacker.Card.IsCreature)
            throw new RuleViolationException($"'{attacker.Card.Name}' is not a creature and cannot attack.");
        if (attacker.IsTapped)
            throw new RuleViolationException($"'{attacker.Card.Name}' is tapped.");
        if (attacker.IsSummoningSick)
            throw new RuleViolationException($"'{attacker.Card.Name}' has summoning sickness and cannot attack yet.");
        return attacker;
    }

    private CardInstance RequireReadyBlocker(Player defender, int index)
    {
        if (index < 0 || index >= defender.BattleZone.Count)
            throw new RuleViolationException("The blocker index is out of range of the defender's battle zone.");
        var blocker = defender.BattleZone[index];
        if (!blocker.Card.IsCreature)
            throw new RuleViolationException($"'{blocker.Card.Name}' is not a creature and cannot block.");
        if (!blocker.Card.HasKeyword(Keyword.Blocker))
            throw new RuleViolationException($"'{blocker.Card.Name}' does not have the Blocker keyword and cannot block.");
        if (blocker.IsTapped)
            throw new RuleViolationException($"'{blocker.Card.Name}' is tapped and cannot block.");
        // A blocker assigned summoning sickness may still block (it only stops attacks).
        return blocker;
    }

    private void PayManaFor(Player player, Card card)
    {
        var available = player.ManaZone.Where(m => !m.IsTapped).ToList();
        if (available.Count < card.ManaCost)
            throw new RuleViolationException(
                $"'{card.Name}' costs {card.ManaCost} mana but you only have {available.Count} untapped mana.");

        // A card can only be played when at least one tapped card of the same
        // civilization is available (the "at least 1 matching civilization" rule).
        var matching = available.Where(m => m.Card.Civilization == card.Civilization).ToList();
        if (matching.Count == 0)
            throw new RuleViolationException(
                $"'{card.Name}' requires at least 1 {card.Civilization} mana, but you have no untapped {card.Civilization} mana.");

        // Spend the required number of mana, preferring cards of the card's own
        // civilization. Note: this is a structural simplification of Duel Masters'
        // multi-civilization "mana number" rule - a single-civilization card only
        // needs one matching mana, and we do not model dual/multi-colored mana yet.
        var other = available.Where(m => m.Card.Civilization != card.Civilization).ToList();
        var spent = new List<CardInstance>();
        var fromMatch = Math.Min(matching.Count, card.ManaCost);
        spent.AddRange(matching.Take(fromMatch));
        spent.AddRange(other.Take(card.ManaCost - fromMatch));

        if (spent.Count < card.ManaCost)
            throw new RuleViolationException("Not enough mana to play this card.");

        foreach (var m in spent)
            m.IsTapped = true;
    }

    private void DestroyCreature(CardInstance c)
    {
        var owner = c.Owner
            ?? throw new RuleViolationException($"'{c.Card.Name}' has no recorded owner.");
        owner.Graveyard.Add(c);
        c.Zone = Zone.Graveyard;
        c.IsTapped = false;
        c.IsSummoningSick = false;
        owner.BattleZone.Remove(c);
    }

    private static void RequireHandCard(Player player, int index)
    {
        if (index < 0 || index >= player.Hand.Count)
            throw new RuleViolationException("The hand index is out of range.");
    }

    // ------------------------------------------------------------ deck / util

    private void DrawToHand(Player p, int amount)
    {
        for (var i = 0; i < amount && p.Deck.Count > 0; i++)
        {
            var card = p.Deck[0];
            p.Deck.RemoveAt(0);
            p.Hand.Add(new CardInstance(card, p) { Zone = Zone.Hand });
        }
    }

    private void CheckDeckOut(Player p)
    {
        if (p.Deck.Count == 0 && Winner is null)
            Winner = p == Player1 ? Player2 : Player1;
    }

    private static CardInstance TakeFromHand(List<CardInstance> hand, int index)
    {
        var c = hand[index];
        hand.RemoveAt(index);
        return c;
    }

    private void EnsureMain() => EnsureTurnPhase(GamePhase.Main);

    private void EnsureTurnPhase(GamePhase required)
    {
        if (Phase != required)
            throw new RuleViolationException($"This action is only allowed during {required}. Current phase: {Phase}.");
        if (IsGameOver)
            throw new RuleViolationException("The game is already over.");
    }

    private void EnsureNotOver()
    {
        if (IsGameOver)
            throw new RuleViolationException("The game is already over.");
    }
}
