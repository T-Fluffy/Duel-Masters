using System;
using System.Collections.Generic;
using System.Linq;

namespace DuelMasters.Domain;

/// <summary>A targeted spell / on-play choice: the owning player and a battle-zone index.</summary>
public readonly record struct SpellTarget(Player Owner, int Index);

/// <summary>
/// Turn-by-turn rules engine for a two-player Duel Masters game.
///
/// Pure and engine-independent: the Godot client, backend, AI and tests all share
/// this logic. It enforces the phase flow - Untap, Draw, Main (mana -> summon ->
/// attacks), End - with shield breaking and the shield-trigger interrupt window,
/// plus the card keyword / effect catalogue rules implemented by each card.
/// </summary>
public sealed class DuelGame
{
    private int _activeIndex;
    private bool _manaChargedThisTurn;
    private bool _hasAttackedThisTurn;
    private bool _castSpellThisTurn;
    private readonly List<CardInstance> _pendingShieldTriggers = new();
    private Player? _shieldTriggerOwner;

    /// <summary>
    /// Effects granted by a tap ability that persist until the end of the current
    /// turn ("at the end of this turn ...", "whenever ... this turn, ..."). Resolved
    /// during the end step and cleared when the turn ends.
    /// </summary>
    private readonly List<PendingTurnEffect> _pendingTurnEffects = new();

    private sealed record PendingTurnEffect(EffectId Id, Player Owner, string Race);

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

    /// <summary>
    /// True while a broken shield with the Shield Trigger keyword waits to be played
    /// for free (or declined) by <see cref="ShieldTriggerOwner"/>. No other action may
    /// take place until the window is resolved.
    /// </summary>
    public bool ShieldTriggerWindowActive => _pendingShieldTriggers.Count > 0;

    /// <summary>The player allowed to play the pending shield triggers (the defender).</summary>
    public Player? ShieldTriggerOwner => _shieldTriggerOwner;

    /// <summary>The broken Shield Trigger cards awaiting a free-play decision, in break order.</summary>
    public IReadOnlyList<CardInstance> PendingShieldTriggers => _pendingShieldTriggers;

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
        foreach (var c in active.BattleZone) c.TempPower = 0;
        foreach (var c in active.BattleZone) c.AttackedThisTurn = false;
        _manaChargedThisTurn = false;
        _hasAttackedThisTurn = false;
        _castSpellThisTurn = false;

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

    /// <summary>
    /// End the Main phase, moving to the End phase before the turn ends. Refuses to
    /// end while a ready creature that "attacks each turn" still has a legal attack.
    /// </summary>
    public void EndMainPhase()
    {
        EnsureTurnPhase(GamePhase.Main);
        var mustAttack = MustAttackList();
        if (mustAttack.Count > 0)
            throw new RuleViolationException(
                $"'{string.Join(", ", mustAttack.Select(m => m.Card.Name))}' must attack before you can end the turn (attacks each turn if able).");
        Phase = GamePhase.End;
    }

    /// <summary>End the active player's turn and advance to the opponent.</summary>
    public void EndTurn()
    {
        EnsureTurnPhase(GamePhase.End);

        // Survivor: at the end of each turn, a creature with Survivor that is its
        // owner's only creature in the battle zone is destroyed.
        foreach (var p in new[] { Player1, Player2 })
        {
            if (p.BattleZone.Count == 1)
            {
                var only = p.BattleZone[0];
                if (only.Card.HasKeyword(Keyword.Survivor))
                    DestroyCreature(only);
            }
        }

        // Tap-ability "at the end of this turn" effects resolve here.
        ResolvePendingTurnEffects();

        // "Until end of turn" effects (tap-ability grants, temporary power boosts,
        // etc.) expire here.
        foreach (var p in new[] { Player1, Player2 })
        {
            foreach (var c in p.BattleZone)
            {
                c.ClearEndOfTurnKeywords();
                c.TempPower = 0;
            }
        }
        _pendingTurnEffects.Clear();

        _activeIndex = 1 - _activeIndex;
        TurnNumber++;
        Phase = GamePhase.Untap;
    }

    // ------------------------------------------------------- tap abilities

    /// <summary>
    /// True if the battle-zone creature at <paramref name="index"/> may use one of
    /// its Tap Abilities right now. A Tap Ability can only be used on its owner's
    /// turn, during the Main phase, before any creature has attacked, while the
    /// creature is untapped and not summoning-sick, and never during a Shield Trigger
    /// window or after the game is over. Some abilities additionally need a legal
    /// target to exist before they can be activated.
    /// </summary>
    public bool CanUseTapAbility(Player player, int index)
    {
        if (IsGameOver || Phase != GamePhase.Main || ShieldTriggerWindowActive)
            return false;
        if (!ReferenceEquals(player, ActivePlayer) || _hasAttackedThisTurn)
            return false;
        if (index < 0 || index >= player.BattleZone.Count)
            return false;
        var c = player.BattleZone[index];
        if (!c.Card.HasTapAbility || c.IsTapped || c.IsSummoningSick)
            return false;
        foreach (var eff in c.Card.TapAbilities)
        {
            if (eff.Id == EffectId.Tap_NotModelled)
                continue;
            if (eff.Target == EffectTargetScope.None || TapTargetPool(player, eff).Count > 0)
                return true;
        }
        return false;
    }

    /// <summary>Activate one of the active player's tap-ability creatures with no target.</summary>
    public void ActivateTapAbility(int creatureIndex) => ActivateTapAbility(creatureIndex, null);

    /// <summary>Activate one of the active player's tap-ability creatures, naming its targets and race.</summary>
    public void ActivateTapAbility(int creatureIndex, IReadOnlyList<SpellTarget>? targets, string? race = null)
    {
        EnsureMain();
        EnsureTriggerWindowClosed();
        if (_hasAttackedThisTurn)
            throw new RuleViolationException("You cannot use a tap ability after a creature has attacked.");
        if (creatureIndex < 0 || creatureIndex >= ActivePlayer.BattleZone.Count)
            throw new RuleViolationException("The creature index is out of range of the battle zone.");
        var creature = ActivePlayer.BattleZone[creatureIndex];
        if (!creature.Card.HasTapAbility)
            throw new RuleViolationException($"'{creature.Card.Name}' has no tap ability.");
        if (creature.IsTapped)
            throw new RuleViolationException($"'{creature.Card.Name}' is already tapped.");
        if (creature.IsSummoningSick)
            throw new RuleViolationException($"'{creature.Card.Name}' has summoning sickness and cannot use its tap ability.");
        if (creature.Card.TapAbilities.All(e => e.Id == EffectId.Tap_NotModelled))
            throw new RuleViolationException($"'{creature.Card.Name}' has a tap ability that is not modelled yet.");

        // An ability that says "choose a race" needs the caller to name one of the
        // races currently present in either battle zone (the engine-computed pool).
        if (creature.Card.TapAbilities.Any(e => IsRaceChoosingEffect(e.Id)))
        {
            var choices = LegalRaceChoices(ActivePlayer);
            if (string.IsNullOrWhiteSpace(race) || !choices.Contains(race, StringComparer.OrdinalIgnoreCase))
                throw new RuleViolationException($"'{creature.Card.Name}' needs a race in the battle zone for its tap ability.");
        }

        // Tapping the creature pays the activation cost. Because it is now tapped it
        // can no longer attack this turn, but the other creatures may still attack,
        // so this deliberately does NOT flip the "has attacked" lock.
        creature.Tap();
        ResolveTapAbilities(creature, targets, race);
    }

    /// <summary>Activate one of <paramref name="actor"/>'s tap-ability creatures (used by the AI and tests).</summary>
    public void ActivateTapAbility(int creatureIndex, Player actor, IReadOnlyList<SpellTarget>? targets, string? race = null)
    {
        if (!ReferenceEquals(actor, ActivePlayer))
            throw new RuleViolationException("You may only use your own creatures' tap abilities.");
        ActivateTapAbility(creatureIndex, targets, race);
    }

    /// <summary>
    /// Resolve every Tap Ability on the creature in the order printed. Effects that
    /// need a target consume the caller-supplied targets in order; each must name a
    /// legal card in the zone the effect targets.
    /// </summary>
    private void ResolveTapAbilities(CardInstance creature, IReadOnlyList<SpellTarget>? targets, string? race)
    {
        var cursor = 0;
        foreach (var eff in creature.Card.TapAbilities)
        {
            if (eff.Target == EffectTargetScope.None)
            {
                ResolveTapEffect(ActivePlayer, eff, race);
                continue;
            }

            if (targets is null || cursor >= targets.Count)
                throw new RuleViolationException($"'{creature.Card.Name}' needs a target for its tap ability.");
            var t = targets[cursor++];
            var target = ResolveTapTarget(eff, t.Owner, t.Index);
            ResolveTapTargetedEffect(eff, target);
        }
    }

    private void ResolveTapEffect(Player actor, CardEffect eff, string? race)
    {
        switch (eff.Id)
        {
            case EffectId.Tap_Draw:
                DrawToHand(actor, Math.Max(0, eff.Value));
                break;

            case EffectId.Tap_ChargeMana:
                ChargeTopOfDeck(actor);
                break;

            case EffectId.Tap_DiscardRandom:
                DiscardRandom(OpponentOf(actor), Math.Max(0, eff.Value));
                break;

            case EffectId.Tap_GrantUnblockableCivEot:
                foreach (var c in actor.BattleZone.Where(c => CivMatches(c.Card.Civilization, eff.Data)))
                    c.GainKeywordUntilEndOfTurn(Keyword.Unblockable);
                break;

            case EffectId.Tap_GrantCanAttackUntappedCivEot:
                foreach (var c in actor.BattleZone.Where(c => CivMatches(c.Card.Civilization, eff.Data)))
                    c.GainKeywordUntilEndOfTurn(Keyword.CanAttackUntappedCreatures);
                break;

            // Gandar, Seeker of Explosions: untap all of the owner's {civ} creatures
            // at the end of this turn.
            case EffectId.Tap_UntapOwnCivEot:
                _pendingTurnEffects.Add(new PendingTurnEffect(EffectId.Tap_UntapOwnCivEot, actor, eff.Data));
                break;

            // Tra Rion, Penumbra Guardian: at the end of this turn, untap all
            // creatures of the chosen race in the battle zone.
            case EffectId.Tap_ChooseRaceUntapEot:
                _pendingTurnEffects.Add(new PendingTurnEffect(EffectId.Tap_ChooseRaceUntapEot, actor, race!));
                break;

            // Venom Worm: each creature of the chosen race gets Slayer until the
            // end of the turn (the normal end-of-turn keyword expiry cleans up).
            case EffectId.Tap_ChooseRaceGrantSlayerEot:
                foreach (var p in new[] { actor, OpponentOf(actor) })
                    foreach (var c in p.BattleZone.Where(c => RaceEquals(c.Card.Race, race)))
                        c.GainKeywordUntilEndOfTurn(Keyword.Slayer);
                break;

            // Hokira: whenever one of the owner's creatures of the chosen race would
            // be destroyed this turn, return it to hand instead (checked on destroy).
            case EffectId.Tap_ChooseRaceToHandEot:
                _pendingTurnEffects.Add(new PendingTurnEffect(EffectId.Tap_ChooseRaceToHandEot, actor, race!));
                break;

            // Bliss Totem: move up to {Value} cards from your graveyard into your
            // mana zone ("up to" is simplified to "all present up to the cap").
            case EffectId.Tap_GraveToMana:
                MoveGraveToMana(actor, Math.Max(0, eff.Value));
                break;

            // Tangle Fist: move up to {Value} cards from your hand into your mana
            // zone (simplified the same way as Bliss Totem).
            case EffectId.Tap_HandToMana:
                MoveHandToMana(actor, Math.Max(0, eff.Value));
                break;

            // Sky Crusher: each player puts a card from his mana zone into his
            // graveyard. The card is chosen deterministically (the top of the zone).
            case EffectId.Tap_ManaToGrave:
                MoveManaToGrave(Player1);
                MoveManaToGrave(Player2);
                break;
        }
    }

    private void ResolveTapTargetedEffect(CardEffect eff, CardInstance target)
    {
        switch (eff.Id)
        {
            case EffectId.Tap_ReturnToHand:
                ReturnToHand(target);
                break;

            case EffectId.Tap_TapOpponentCreature:
                target.Tap();
                break;

            case EffectId.Tap_ReturnSpellFromManaToHand:
            case EffectId.Tap_ReturnCreatureFromManaToHand:
            case EffectId.Tap_ReturnManaCardToHand:
            case EffectId.Tap_ReturnGraveCreatureToHand:
                ReturnToHand(target);
                break;

            case EffectId.Tap_DestroyPowerAtMost:
            case EffectId.Tap_DestroyBlocker:
                DestroyCreature(target);
                break;

            case EffectId.Tap_BoostPowerEot:
                target.TempPower += eff.Value;
                break;

            case EffectId.Tap_GrantUnblockableEot:
                target.GainKeywordUntilEndOfTurn(Keyword.Unblockable);
                break;

            case EffectId.Tap_GrantSlayerEot:
                target.GainKeywordUntilEndOfTurn(Keyword.Slayer);
                break;

            case EffectId.Tap_GrantSpeedAttackerEot:
                target.GainKeywordUntilEndOfTurn(Keyword.SpeedAttacker);
                break;

            case EffectId.Tap_GrantDoubleBreakerEot:
                target.GainKeywordUntilEndOfTurn(Keyword.DoubleBreaker);
                break;
        }
    }

    /// <summary>
    /// Resolve every tap-ability effect deferred to the end of the current turn
    /// (Gandar untaps Light, Tra Rion untaps the chosen race). Runs during the end
    /// step, before temporary keywords and power boosts expire.
    /// </summary>
    private void ResolvePendingTurnEffects()
    {
        foreach (var pending in _pendingTurnEffects)
        {
            switch (pending.Id)
            {
                case EffectId.Tap_UntapOwnCivEot:
                    foreach (var c in pending.Owner.BattleZone.Where(c => CivMatches(c.Card.Civilization, pending.Race)))
                        c.Untap();
                    break;

                case EffectId.Tap_ChooseRaceUntapEot:
                    foreach (var p in new[] { pending.Owner, OpponentOf(pending.Owner) })
                        foreach (var c in p.BattleZone.Where(c => RaceEquals(c.Card.Race, pending.Race)))
                            c.Untap();
                    break;
            }
        }
    }

    /// <summary>The separate zone list the effect's target is chosen from.</summary>
    private static List<CardInstance> TapTargetZone(Player owner, EffectTargetScope scope) => scope switch
    {
        EffectTargetScope.OwnManaZone or EffectTargetScope.OpponentManaZone => owner.ManaZone,
        EffectTargetScope.OwnGraveyard => owner.Graveyard,
        _ => owner.BattleZone,
    };

    private CardInstance ResolveTapTarget(CardEffect eff, Player targetOwner, int targetIndex)
    {
        if (targetIndex < 0 || targetIndex >= TapTargetZone(targetOwner, eff.Target).Count)
            throw new RuleViolationException("The tap-ability target index is out of range.");
        var target = TapTargetZone(targetOwner, eff.Target)[targetIndex];

        switch (eff.Target)
        {
            case EffectTargetScope.OwnCreature:
            case EffectTargetScope.OwnManaZone:
            case EffectTargetScope.OwnGraveyard:
                if (!ReferenceEquals(targetOwner, ActivePlayer))
                    throw new RuleViolationException("You may only target one of your own cards.");
                break;
            case EffectTargetScope.OpponentCreature:
            case EffectTargetScope.OpponentManaZone:
                if (ReferenceEquals(targetOwner, ActivePlayer))
                    throw new RuleViolationException("You must target one of your opponent's cards.");
                break;
            case EffectTargetScope.AnyCreature:
                if (!ReferenceEquals(targetOwner, ActivePlayer) && !ReferenceEquals(targetOwner, Opponent))
                    throw new RuleViolationException("That target is not in this game.");
                break;
        }

        if (!IsLegalTapTarget(eff, target))
            throw new RuleViolationException($"'{target.Card.Name}' cannot be targeted by this tap ability.");
        return target;
    }

    /// <summary>True when <paramref name="target"/> is in the effect's legal target pool.</summary>
    public bool IsLegalTapTarget(CardEffect eff, CardInstance target)
        => TapTargetPool(ActivePlayer, eff).Contains(target);

    /// <summary>
    /// The cards a tap-ability effect may choose from: the effect's zone (battle
    /// zone, mana zone, or graveyard) restricted to the owner the effect names,
    /// filtered by any power / type / keyword / civilization requirement.
    /// </summary>
    public IReadOnlyList<CardInstance> TapTargetPool(Player player, CardEffect eff)
    {
        IEnumerable<CardInstance> pool = eff.Target switch
        {
            EffectTargetScope.OwnCreature => player.BattleZone,
            EffectTargetScope.OpponentCreature => OpponentOf(player).BattleZone,
            EffectTargetScope.AnyCreature => player.BattleZone.Concat(OpponentOf(player).BattleZone),
            EffectTargetScope.OwnManaZone => player.ManaZone,
            EffectTargetScope.OpponentManaZone => OpponentOf(player).ManaZone,
            EffectTargetScope.OwnGraveyard => player.Graveyard,
            _ => Array.Empty<CardInstance>(),
        };

        switch (eff.Id)
        {
            case EffectId.Tap_DestroyPowerAtMost:
                return pool.Where(t => CurrentPower(t) <= eff.Value).ToList();
            case EffectId.Tap_DestroyBlocker:
                return pool.Where(t => t.Card.HasKeyword(Keyword.Blocker)).ToList();
            case EffectId.Tap_ReturnSpellFromManaToHand:
                return pool.Where(t => t.Card.CardType == CardType.Spell).ToList();
            case EffectId.Tap_ReturnCreatureFromManaToHand:
                return pool.Where(t => t.Card.IsCreature).ToList();
            case EffectId.Tap_ReturnGraveCreatureToHand:
                return pool.Where(t => t.Card.IsCreature && CivMatches(t.Card.Civilization, eff.Data)).ToList();
            case EffectId.Tap_GrantDoubleBreakerEot:
                return pool.Where(t => CivMatches(t.Card.Civilization, eff.Data)).ToList();
            case EffectId.Tap_TapOpponentCreature:
                return pool.Where(t => !t.IsTapped).ToList();
            default:
                return pool.ToList();
        }
    }

    // -------------------------------------------------- main phase actions

    /// <summary>
    /// Deposit one hand card into the mana zone. Charged mana is available
    /// immediately (Duel Masters rule: only multicolored cards enter the mana zone
    /// tapped), so the freshly charged card can pay for a summon or spell in this
    /// same main phase - including the very first mana charge of the game. A player
    /// may charge at most one card per turn by default; extra charges only come from
    /// card effects (Mana Acceleration) during a later phase.
    /// </summary>
    public void PlayManaToManaZone(int handIndex)
    {
        EnsureTurnPhase(GamePhase.Main);
        EnsureTriggerWindowClosed();
        if (_manaChargedThisTurn)
            throw new RuleViolationException("You may only charge one mana card per turn.");
        RequireHandCard(ActivePlayer, handIndex);
        var card = TakeFromHand(ActivePlayer.Hand, handIndex);
        card.Zone = Zone.ManaZone;
        ActivePlayer.ManaZone.Add(card);
        _manaChargedThisTurn = true;
    }

    /// <summary>True if the player can tap enough untapped mana to play the card.</summary>
    public bool CanAfford(Player player, Card card)
    {
        return player.ManaZone.Count(m => !m.IsTapped) >= CardManaCost(player, card);
    }

    /// <summary>
    /// True if <see cref="PayManaFor"/> will succeed for this card: enough untapped
    /// mana AND at least one untapped mana of the card's own civilization.
    /// </summary>
    public bool CanPlay(Player player, Card card)
    {
        var available = player.ManaZone.Where(m => !m.IsTapped).ToList();
        if (available.Count < CardManaCost(player, card))
            return false;
        return available.Any(m => m.Card.Civilization == card.Civilization);
    }

    /// <summary>
    /// True when a creature is actually summonable: it is a creature, affordable,
    /// and (for "summon only if you cast a spell this turn" cards) a spell was cast.
    /// </summary>
    public bool CanSummon(Player player, Card card)
    {
        if (card.CardType == CardType.Spell)
            return false;
        if (card.IsEvolution)
            return false; // evolution creatures cannot be summoned directly
        if (!CanPlay(player, card))
            return false;
        return !card.HasKeyword(Keyword.SummonRequiresSpellCast) || _castSpellThisTurn;
    }

    /// <summary>
    /// Summon a creature from the active player's hand into the battle zone, paying
    /// mana. It is summoning-sick (can't attack) unless it has Speed Attacker.
    /// On-play targeted effects resolve only when a target is supplied (the card's
    /// "when you put this creature into the battle zone, ..." abilities).
    /// </summary>
    public CardInstance SummonCreature(int handIndex)
    {
        return SummonCreatureInternal(ActivePlayer, handIndex, null);
    }

    /// <summary>Summon a creature whose on-play ability targets a battle-zone creature.</summary>
    public CardInstance SummonCreature(int handIndex, Player targetOwner, int targetIndex)
    {
        return SummonCreatureInternal(ActivePlayer, handIndex, new SpellTarget(targetOwner, targetIndex));
    }

    private CardInstance SummonCreatureInternal(Player actor, int handIndex, SpellTarget? onPlayTarget)
    {
        EnsureTurnPhase(GamePhase.Main);
        EnsureTriggerWindowClosed();
        if (_hasAttackedThisTurn)
            throw new RuleViolationException("You cannot summon a creature after a creature has attacked.");
        RequireHandCard(actor, handIndex);
        var card = actor.Hand[handIndex];
        if (!card.Card.IsCreature)
            throw new RuleViolationException($"'{card.Card.Name}' is not a creature.");
        if (card.Card.IsEvolution)
            throw new RuleViolationException(
                $"'{card.Card.Name}' is an Evolution creature and must be placed on a {card.Card.EvolutionOf} creature instead of being summoned.");
        if (card.Card.HasKeyword(Keyword.SummonRequiresSpellCast) && !_castSpellThisTurn)
            throw new RuleViolationException($"You can summon '{card.Card.Name}' only if you have cast a spell this turn.");
        if (onPlayTarget is { } t && !IsLegalOnPlayTarget(card.Card, actor, t.Owner, t.Index))
            throw new RuleViolationException($"'{card.Card.Name}' cannot target that creature.");

        PayManaFor(actor, card.Card);
        var instance = actor.Hand[handIndex];
        instance.Zone = Zone.BattleZone;
        instance.IsSummoningSick = !card.Card.HasKeyword(Keyword.SpeedAttacker);
        actor.Hand.RemoveAt(handIndex);
        actor.BattleZone.Add(instance);
        ResolveBattleZoneEntry(instance, onPlayTarget?.Owner, onPlayTarget?.Index);
        return instance;
    }

    // -------------------------------------------------- evolution

    /// <summary>
    /// True when <paramref name="candidate"/> is a legal base for
    /// <paramref name="evolution"/>: a creature whose race matches the evolution
    /// card's required race.
    /// </summary>
    public static bool IsEvolutionBase(Card evolution, Card candidate)
        => candidate.IsCreature && RaceMatches(candidate.Race, evolution.EvolutionOf);

    /// <summary>True if the player controls at least one creature that the evolution card can be placed on.</summary>
    public bool HasEvolutionBase(Player player, Card evolution)
        => player.BattleZone.Any(c => IsEvolutionBase(evolution, c.Card));

    /// <summary>
    /// Finds a battle-zone creature owned by <paramref name="player"/> that
    /// <paramref name="evolution"/> may be evolved onto (its race must match the
    /// evolution card's required race).
    /// </summary>
    public bool TryGetEvolutionBase(Player player, Card evolution, out int index)
    {
        for (var i = 0; i < player.BattleZone.Count; i++)
        {
            if (IsEvolutionBase(evolution, player.BattleZone[i].Card))
            {
                index = i;
                return true;
            }
        }
        index = -1;
        return false;
    }

    /// <summary>
    /// True when the evolution creature can actually be evolved this turn: it is an
    /// evolution, the player controls a matching-race creature, it costs no mana,
    /// and no creature has attacked yet.
    /// </summary>
    public bool CanEvolve(Player player, Card card)
        => card.IsEvolution && !_hasAttackedThisTurn && HasEvolutionBase(player, card);

    /// <summary>Evolve an evolution creature from the hand onto one of the active player's battle-zone creatures.</summary>
    public CardInstance EvolveCreature(int handIndex, int baseIndex)
        => EvolveCreatureInternal(ActivePlayer, handIndex, baseIndex, null);

    /// <summary>Evolve onto a base creature, resolving the evolution's on-play targeted effect against one creature.</summary>
    public CardInstance EvolveCreature(int handIndex, int baseIndex, Player targetOwner, int targetIndex)
        => EvolveCreatureInternal(ActivePlayer, handIndex, baseIndex, new SpellTarget(targetOwner, targetIndex));

    private CardInstance EvolveCreatureInternal(Player actor, int handIndex, int baseIndex, SpellTarget? onPlayTarget)
    {
        EnsureTurnPhase(GamePhase.Main);
        EnsureTriggerWindowClosed();
        if (_hasAttackedThisTurn)
            throw new RuleViolationException("You cannot place an evolution creature after a creature has attacked.");
        RequireHandCard(actor, handIndex);
        var card = actor.Hand[handIndex];
        if (!card.Card.IsEvolution)
            throw new RuleViolationException("Only an Evolution creature can be evolved.");
        if (baseIndex < 0 || baseIndex >= actor.BattleZone.Count)
            throw new RuleViolationException("There is no creature to evolve onto.");
        var baseCard = actor.BattleZone[baseIndex];
        if (!IsEvolutionBase(card.Card, baseCard.Card))
            throw new RuleViolationException($"'{card.Card.Name}' can only be placed on a {card.Card.EvolutionOf} creature.");
        if (onPlayTarget is { } t && !IsLegalOnPlayTarget(card.Card, actor, t.Owner, t.Index))
            throw new RuleViolationException($"'{card.Card.Name}' cannot target that creature.");

        var instance = actor.Hand[handIndex];
        instance.IsSummoningSick = !card.Card.HasKeyword(Keyword.SpeedAttacker);
        actor.Hand.RemoveAt(handIndex);

        // The whole stack beneath the base slides under the new top (supports
        // evolution-on-evolution stacks), and the stack re-enters where the base was.
        instance.Underneath.Add(baseCard);
        instance.Underneath.AddRange(baseCard.Underneath);
        baseCard.Underneath.Clear();
        baseCard.Zone = Zone.Underneath;

        actor.BattleZone.RemoveAt(baseIndex);
        actor.BattleZone.Insert(baseIndex, instance);
        instance.Zone = Zone.BattleZone;
        ResolveBattleZoneEntry(instance, onPlayTarget?.Owner, onPlayTarget?.Index);
        return instance;
    }

    /// <summary>
    /// Cast a spell from the active player's hand (paid and then sent to the
    /// graveyard - or to the mana zone when the spell has the Charger keyword),
    /// resolving its effects. Effects that need a creature target fizzle when the
    /// caller supplies none (the spell still resolves).
    /// </summary>
    public CardInstance CastSpell(int handIndex)
    {
        return CastSpellInternal(ActivePlayer, handIndex, null);
    }

    /// <summary>Cast a targeted spell resolving its first targeting effect against one creature.</summary>
    public CardInstance CastSpell(int handIndex, Player targetOwner, int targetIndex)
    {
        return CastSpellInternal(ActivePlayer, handIndex, new[] { new SpellTarget(targetOwner, targetIndex) });
    }

    /// <summary>Cast a targeted spell, resolving each targeting effect against the supplied targets.</summary>
    public CardInstance CastSpell(int handIndex, IReadOnlyList<SpellTarget> targets)
    {
        return CastSpellInternal(ActivePlayer, handIndex, targets);
    }

    private CardInstance CastSpellInternal(Player actor, int handIndex, IReadOnlyList<SpellTarget>? targets)
    {
        EnsureTurnPhase(GamePhase.Main);
        EnsureTriggerWindowClosed();
        if (_hasAttackedThisTurn)
            throw new RuleViolationException("You cannot cast a spell after a creature has attacked.");
        if (!ReferenceEquals(actor, ActivePlayer))
            throw new RuleViolationException("Only the active player may cast a spell.");
        RequireHandCard(actor, handIndex);
        var card = actor.Hand[handIndex];
        if (card.Card.CardType != CardType.Spell)
            throw new RuleViolationException($"'{card.Card.Name}' is not a spell.");
        EnsureLegalTargets(actor, card.Card, targets);

        PayManaFor(actor, card.Card);
        var instance = TakeFromHand(actor.Hand, handIndex);
        if (card.Card.HasKeyword(Keyword.Charger))
        {
            instance.Zone = Zone.ManaZone;
            actor.ManaZone.Add(instance);
        }
        else
        {
            instance.Zone = Zone.Graveyard;
            actor.Graveyard.Add(instance);
        }
        _castSpellThisTurn = true;
        ResolveSpellEffects(actor, instance, targets);
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
    public void AttackPlayer(int attackerIndex, Player? blockerOwner = null, int? blockerIndex = null)
    {
        ValidatePlayerAttack(attackerIndex);
        var active = ActivePlayer;
        var defender = Opponent;
        var attacker = active.BattleZone[attackerIndex];

        attacker.IsTapped = true;
        attacker.AttackedThisTurn = true;
        _hasAttackedThisTurn = true;

        if (blockerOwner is not null && blockerIndex is int bIdx)
        {
            if (!ReferenceEquals(blockerOwner, defender))
                throw new RuleViolationException("Only the defending player may block this attack.");
            var blocker = RequireReadyBlocker(defender, bIdx, attacker);
            blocker.IsTapped = true;
            Battle(attacker, blocker);
            return;
        }

        if (defender.ShieldCount == 0)
        {
            Winner = active;
            return;
        }

        BreakShields(defender, BreakerCount(attacker));
    }

    /// <summary>
    /// The active player's creature at <paramref name="attackerIndex"/> attacks a
    /// specific creature in the defender's battle zone directly. Under normal rules
    /// only a tapped enemy creature may be attacked this way (creatures with the
    /// "can attack untapped creatures" keyword may attack any). Higher power wins;
    /// equal power destroys both.
    /// </summary>
    public void AttackCreature(int attackerIndex, int targetIndex)
    {
        EnsureMain();
        EnsureTriggerWindowClosed();
        var active = ActivePlayer;
        var defender = Opponent;
        var attacker = RequireReadyAttacker(active, attackerIndex);
        if (attacker.HasKeywordNow(Keyword.CannotAttackCreatures))
            throw new RuleViolationException($"'{attacker.Card.Name}' can't attack creatures.");

        if (targetIndex < 0 || targetIndex >= defender.BattleZone.Count)
            throw new RuleViolationException("The target index is out of range of the defender's battle zone.");
        var target = defender.BattleZone[targetIndex];
        if (!target.Card.IsCreature)
            throw new RuleViolationException($"'{target.Card.Name}' is not a creature and cannot be attacked.");
        if (target.HasKeywordNow(Keyword.CannotBeAttacked))
            throw new RuleViolationException($"'{target.Card.Name}' can't be attacked.");
        if (!target.IsTapped && !attacker.HasKeywordNow(Keyword.CanAttackUntappedCreatures))
            throw new RuleViolationException("Under normal rules you may only attack a tapped creature.");

        attacker.IsTapped = true;
        attacker.AttackedThisTurn = true;
        _hasAttackedThisTurn = true;

        Battle(attacker, target);
    }

    /// <summary>
    /// Resolve a battle between two creatures: the higher power survives and the
    /// loser is sent to its owner's graveyard; equal power destroys both. The
    /// attacking creature is tapped, and a blocker used to intercept is tapped.
    ///
    /// A defender with the Slayer keyword destroys the attacker even when it loses
    /// the battle (both die). While attacking, Power Attacker and similar
    /// attack-time modifiers add to the attacker's power.
    /// </summary>
    private void Battle(CardInstance attacker, CardInstance defender)
    {
        var aPower = CurrentPower(attacker) + AttackPowerBoost(attacker);
        var dPower = CurrentPower(defender);

        if (aPower > dPower)
            DestroyCreature(defender);
        else if (dPower > aPower)
            DestroyCreature(attacker);
        else
        {
            DestroyCreature(defender);
            DestroyCreature(attacker);
        }

        // Slayer: a creature that has Slayer destroys the other creature in the
        // battle even when it loses the power battle (both sides can kill each other).
        if (defender.HasKeywordNow(Keyword.Slayer))
            DestroyCreature(attacker);
        if (attacker.HasKeywordNow(Keyword.Slayer))
            DestroyCreature(defender);
    }

    /// <summary>True when the opponent may block this attacking creature.</summary>
    public static bool CanBeBlocked(Card attacker)
    {
        return !attacker.HasKeyword(Keyword.Unblockable) && !attacker.HasKeyword(Keyword.Stealth);
    }

    /// <summary>True when the opponent may block this creature (including temporary keywords).</summary>
    public static bool CanBeBlocked(CardInstance attacker)
    {
        return !attacker.HasKeywordNow(Keyword.Unblockable) && !attacker.HasKeywordNow(Keyword.Stealth);
    }

    /// <summary>
    /// Validate that the active player's creature at <paramref name="attackerIndex"/>
    /// may attack the defending player right now, without mutating the game. Throws a
    /// <see cref="RuleViolationException"/> when the attack is not legal.
    /// </summary>
    public void ValidatePlayerAttack(int attackerIndex)
    {
        EnsureMain();
        EnsureTriggerWindowClosed();
        var active = ActivePlayer;
        var attacker = RequireReadyAttacker(active, attackerIndex);
        RequireCanAttackPlayers(attacker);
    }

    /// <summary>
    /// The number of untapped enemy creatures with the Blocker keyword that could
    /// currently intercept the active player's attack at <paramref name="attackerIndex"/>,
    /// or 0 when the attacker itself cannot be blocked.
    /// </summary>
    public int ReadyBlockerChoices(int attackerIndex)
    {
        EnsureMain();
        EnsureTriggerWindowClosed();
        var active = ActivePlayer;
        var attacker = RequireReadyAttacker(active, attackerIndex);
        RequireCanAttackPlayers(attacker);
        if (!CanBeBlocked(attacker))
            return 0;

        var count = 0;
        foreach (var candidate in Opponent.BattleZone)
        {
            if (candidate.Card.IsCreature
                && candidate.Card.HasKeyword(Keyword.Blocker)
                && !candidate.IsTapped)
                count++;
        }
        return count;
    }

    private static void RequireCanAttackPlayers(CardInstance attacker)
    {
        if (attacker.HasKeywordNow(Keyword.CannotAttackPlayers))
            throw new RuleViolationException($"'{attacker.Card.Name}' can't attack players.");
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
        OpenShieldTriggerWindow(defender, broken);
        return broken;
    }

    /// <summary>How many shields one hit from this creature breaks (printed plus temporary keywords).</summary>
    private static int BreakerCount(CardInstance attacker) =>
        attacker.HasKeywordNow(Keyword.TripleBreaker) ? 3 :
        attacker.HasKeywordNow(Keyword.DoubleBreaker) ? 2 : 1;

    /// <summary>
    /// Every broken shield is added to the defender's hand. Shields carrying the
    /// Shield Trigger keyword additionally open the free-play interrupt window:
    /// their owner may play them immediately at no cost (see
    /// <see cref="PlayShieldTrigger(int)"/>) or let them stay in hand.
    /// </summary>
    private void OpenShieldTriggerWindow(Player defender, List<Card> broken)
    {
        foreach (var shield in broken)
        {
            var instance = new CardInstance(shield, defender) { Zone = Zone.Hand };
            defender.Hand.Add(instance);
            if (shield.HasKeyword(Keyword.ShieldTrigger))
                _pendingShieldTriggers.Add(instance);
        }
        _shieldTriggerOwner = _pendingShieldTriggers.Count > 0 ? defender : null;
    }

    /// <summary>
    /// Play one pending Shield Trigger card for free as its owner. If it is a
    /// creature it enters the battle zone (summoning-sick unless it has Speed
    /// Attacker); a spell resolves its effects (targeted effects fizzle with no
    /// target - use <see cref="PlayShieldTrigger(int, Player, int)"/> to name one).
    /// </summary>
    public CardInstance PlayShieldTrigger(int handIndex)
    {
        return PlayShieldTriggerInternal(handIndex, null);
    }

    /// <summary>Play a pending Shield Trigger spell targeting a specific creature.</summary>
    public CardInstance PlayShieldTrigger(int handIndex, Player targetOwner, int targetIndex)
    {
        return PlayShieldTriggerInternal(handIndex, new[] { new SpellTarget(targetOwner, targetIndex) });
    }

    /// <summary>Play a pending Shield Trigger spell targeting several creatures.</summary>
    public CardInstance PlayShieldTrigger(int handIndex, IReadOnlyList<SpellTarget> targets)
    {
        return PlayShieldTriggerInternal(handIndex, targets);
    }

    private CardInstance PlayShieldTriggerInternal(int handIndex, IReadOnlyList<SpellTarget>? targets)
    {
        if (!ShieldTriggerWindowActive)
            throw new RuleViolationException("There are no Shield Trigger cards waiting to be played.");
        var owner = _shieldTriggerOwner!;
        if (handIndex < 0 || handIndex >= owner.Hand.Count)
            throw new RuleViolationException("The hand index is out of range.");
        var instance = owner.Hand[handIndex];
        if (!_pendingShieldTriggers.Contains(instance))
            throw new RuleViolationException("Only a broken Shield Trigger card may be played for free.");
        EnsureLegalTargets(owner, instance.Card, targets);

        _pendingShieldTriggers.Remove(instance);
        if (_pendingShieldTriggers.Count == 0)
            _shieldTriggerOwner = null;

        if (instance.Card.IsEvolution)
            throw new RuleViolationException(
                $"'{instance.Card.Name}' is an Evolution creature and cannot be summoned from a shield trigger - it may only be evolved onto a matching creature.");

        if (instance.Card.IsCreature)
        {
            owner.Hand.Remove(instance);
            instance.Zone = Zone.BattleZone;
            instance.IsTapped = false;
            instance.IsSummoningSick = !instance.Card.HasKeyword(Keyword.SpeedAttacker);
            owner.BattleZone.Add(instance);
            ResolveBattleZoneEntry(instance, null, null);
            return instance;
        }

        owner.Hand.Remove(instance);
        if (instance.Card.HasKeyword(Keyword.Charger))
        {
            instance.Zone = Zone.ManaZone;
            owner.ManaZone.Add(instance);
        }
        else
        {
            instance.Zone = Zone.Graveyard;
            owner.Graveyard.Add(instance);
        }
        ResolveSpellEffects(owner, instance, targets);
        return instance;
    }

    /// <summary>Leave the remaining pending Shield Trigger cards in hand and close the window.</summary>
    public void DeclineShieldTriggers()
    {
        _pendingShieldTriggers.Clear();
        _shieldTriggerOwner = null;
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
        if (attacker.HasKeywordNow(Keyword.CannotAttackOutnumbered) && Opponent.BattleZone.Count > active.BattleZone.Count)
            throw new RuleViolationException($"'{attacker.Card.Name}' can't attack while the opponent has more creatures.");
        return attacker;
    }

    private CardInstance RequireReadyBlocker(Player defender, int index, CardInstance attacker)
    {
        if (index < 0 || index >= defender.BattleZone.Count)
            throw new RuleViolationException("The blocker index is out of range of the defender's battle zone.");
        var blocker = defender.BattleZone[index];
        if (!blocker.Card.IsCreature)
            throw new RuleViolationException($"'{blocker.Card.Name}' is not a creature and cannot block.");
        if (!blocker.HasKeywordNow(Keyword.Blocker))
            throw new RuleViolationException($"'{blocker.Card.Name}' does not have the Blocker keyword and cannot block.");
        if (blocker.IsTapped)
            throw new RuleViolationException($"'{blocker.Card.Name}' is tapped and cannot block.");
        if (!CanBeBlocked(attacker))
            throw new RuleViolationException($"'{attacker.Card.Name}' cannot be blocked.");
        // A blocker assigned summoning sickness may still block (it only stops attacks).
        return blocker;
    }

    private void PayManaFor(Player player, Card card)
    {
        var cost = CardManaCost(player, card);
        var available = player.ManaZone.Where(m => !m.IsTapped).ToList();
        if (available.Count < cost)
            throw new RuleViolationException(
                $"'{card.Name}' costs {cost} mana but you only have {available.Count} untapped mana.");

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
        var fromMatch = Math.Min(matching.Count, cost);
        spent.AddRange(matching.Take(fromMatch));
        spent.AddRange(other.Take(cost - fromMatch));

        if (spent.Count < cost)
            throw new RuleViolationException("Not enough mana to play this card.");

        foreach (var m in spent)
            m.IsTapped = true;
    }

    /// <summary>
    /// The effective mana cost of <paramref name="card"/> for <paramref name="player"/>,
    /// after all in-play cost modifiers (never below 1).
    /// </summary>
    public int CardManaCost(Player player, Card card)
    {
        var cost = card.ManaCost;
        var isSpell = card.CardType == CardType.Spell;

        // Own-zone cost reducers ("Your creatures/spells cost N less to summon/cast").
        foreach (var inst in player.BattleZone)
        {
            foreach (var e in inst.Card.Effects)
            {
                if (isSpell)
                {
                    if (e.Id == EffectId.CostDecrease_Cast_All)
                        cost -= e.Value;
                }
                else
                {
                    if (e.Id == EffectId.CostDecrease_Summon_All)
                        cost -= e.Value;
                    if (e.Id == EffectId.CostDecrease_Summon_ByRace && RaceMatches(card.Race, e.Data))
                        cost -= e.Value;
                }
            }
        }

        // Global cost increases ("Each {civ} creature/spell costs N more").
        foreach (var source in new[] { Player1.BattleZone, Player2.BattleZone })
        {
            foreach (var inst in source)
            {
                foreach (var e in inst.Card.Effects)
                {
                    if (isSpell && e.Id == EffectId.CostIncrease_Cast_ByCiv && CivMatches(card.Civilization, e.Data))
                        cost += e.Value;
                    if (!isSpell && e.Id == EffectId.CostIncrease_Summon_ByCiv && CivMatches(card.Civilization, e.Data))
                        cost += e.Value;
                }
            }
        }

        return Math.Max(1, cost);
    }

    private void DestroyCreature(CardInstance c)
    {
        var owner = c.Owner;
        if (owner is null || !owner.BattleZone.Contains(c))
            return; // already destroyed this resolution (e.g. a Slayer double-kill)

        owner.BattleZone.Remove(c);
        c.AttackedThisTurn = false;
        c.IsTapped = false;
        c.IsSummoningSick = false;
        c.TempPower = 0;

        // Cards under an evolution are always destroyed along with it (they never
        // trigger destroyed abilities, and replacement effects on the top do not
        // protect them - they simply go to the graveyard).
        foreach (var under in c.Underneath)
        {
            if (under.Owner is { } uo && under.Zone == Zone.Underneath)
            {
                under.Zone = Zone.Graveyard;
                uo.Graveyard.Add(under);
            }
        }
        c.Underneath.Clear();

        // Hokira's persistent race effect: "Whenever one of your creatures of that
        // race would be destroyed this turn, return it to your hand instead."
        if (_pendingTurnEffects.Any(e =>
                e.Id == EffectId.Tap_ChooseRaceToHandEot
                && ReferenceEquals(e.Owner, owner)
                && RaceEquals(c.Card.Race, e.Race)))
        {
            c.Zone = Zone.Hand;
            owner.Hand.Add(c);
            return;
        }

        // "If this creature would be destroyed, put it into your hand/mana instead."
        if (c.Card.EffectOf(EffectId.OnDestroyed_ToHand) is not null)
        {
            c.Zone = Zone.Hand;
            owner.Hand.Add(c);
            return;
        }
        if (c.Card.EffectOf(EffectId.OnDestroyed_ToMana) is not null)
        {
            c.Zone = Zone.ManaZone;
            owner.ManaZone.Add(c);
            return;
        }

        c.Zone = Zone.Graveyard;
        owner.Graveyard.Add(c);
        ResolveDestroyedTriggers(c);
    }

    private void ResolveDestroyedTriggers(CardInstance c)
    {
        if (c.Card.EffectOf(EffectId.OnDestroyed_Draw) is { } e)
            DrawToHand(c.Owner!, e.Value);
    }

    private void ResolveBattleZoneEntry(CardInstance instance, Player? targetOwner, int? targetIndex)
    {
        var owner = instance.Owner!;
        foreach (var effect in instance.Card.Effects)
        {
            switch (effect.Id)
            {
                case EffectId.OnPlay_Draw:
                    DrawToHand(owner, effect.Value);
                    break;

                case EffectId.OnPlay_ChargeMana:
                    ChargeTopOfDeck(owner);
                    break;

                case EffectId.OnPlay_UntapAllOwnCreatures:
                    foreach (var c in owner.BattleZone)
                        c.Untap();
                    break;

                case EffectId.OnPlay_TapCreature:
                case EffectId.OnPlay_ReturnToHand:
                case EffectId.OnPlay_DestroyPowerAtMost:
                case EffectId.OnPlay_UntapOwnCreature:
                {
                    var target = ResolveCreatureTriggerTarget(effect, owner, targetOwner, targetIndex);
                    if (target is null)
                        continue;
                    ApplyTargetedEffect(effect, target);
                    break;
                }
            }
        }
    }

    /// <summary>True when a creature's on-play powers target a battle-zone creature.</summary>
    public static bool HasOnPlayTargetChoice(Card creature) => creature.Effects.Any(IsOnPlayTargetedEffect);

    private static bool IsOnPlayTargetedEffect(CardEffect e) => e.Id is
        EffectId.OnPlay_TapCreature or
        EffectId.OnPlay_ReturnToHand or
        EffectId.OnPlay_DestroyPowerAtMost or
        EffectId.OnPlay_UntapOwnCreature;

    /// <summary>
    /// True when the battle-zone creature at <paramref name="targetIndex"/> is a
    /// legal target for <paramref name="creature"/>'s first targeted on-play ability,
    /// played by <paramref name="actor"/>.
    /// </summary>
    public bool IsLegalOnPlayTarget(Card creature, Player actor, Player targetOwner, int targetIndex)
    {
        var effect = creature.Effects.FirstOrDefault(IsOnPlayTargetedEffect);
        if (effect is null || effect.Target == EffectTargetScope.None)
            return false;
        if (targetIndex < 0 || targetIndex >= targetOwner.BattleZone.Count)
            return false;
        var target = targetOwner.BattleZone[targetIndex];
        if (!target.Card.IsCreature)
            return false;
        if (effect.Id == EffectId.OnPlay_DestroyPowerAtMost && CurrentPower(target) > effect.Value)
            return false;
        return effect.Target switch
        {
            EffectTargetScope.OwnCreature => ReferenceEquals(targetOwner, actor),
            EffectTargetScope.OpponentCreature => ReferenceEquals(targetOwner, OpponentOf(actor)),
            _ => true,
        };
    }

    private CardInstance? ResolveCreatureTriggerTarget(CardEffect effect, Player actor, Player? targetOwner, int? targetIndex)
    {
        if (targetOwner is null || targetIndex is not int index)
            return null; // no target supplied -> the on-play ability resolves without effect
        if (index < 0 || index >= targetOwner.BattleZone.Count)
            throw new RuleViolationException("The target index is out of range of the battle zone.");
        var target = targetOwner.BattleZone[index];
        if (!target.Card.IsCreature)
            throw new RuleViolationException($"'{target.Card.Name}' is not a creature and cannot be targeted.");
        switch (effect.Target)
        {
            case EffectTargetScope.OwnCreature:
                if (!ReferenceEquals(targetOwner, actor))
                    throw new RuleViolationException("You may only target one of your own creatures.");
                break;
            case EffectTargetScope.OpponentCreature:
                if (ReferenceEquals(targetOwner, actor))
                    throw new RuleViolationException("You may only target one of your opponent's creatures.");
                break;
        }
        if (effect.Id == EffectId.OnPlay_DestroyPowerAtMost && CurrentPower(target) > effect.Value)
            throw new RuleViolationException(
                $"'{target.Card.Name}' has power greater than {effect.Value} and cannot be destroyed.");
        return target;
    }

    /// <summary>
    /// Run a spell's effects for <paramref name="actor"/> (the player playing the
    /// spell - the active player normally, the trigger owner inside a trigger
    /// window). Targeting effects resolve against the supplied target list, in
    /// order; with no target supplied they fizzle harmlessly.
    /// </summary>
    private void ResolveSpellEffects(Player actor, CardInstance spell, IReadOnlyList<SpellTarget>? targets)
    {
        var cursor = 0;
        SpellTarget? Next()
        {
            if (targets is null || cursor >= targets.Count)
                return null;
            return targets[cursor++];
        }

        foreach (var effect in spell.Card.Effects)
        {
            switch (effect.Id)
            {
                case EffectId.Spell_Draw:
                    DrawToHand(actor, effect.Value);
                    break;

                case EffectId.Spell_DestroyAllCreatures:
                    DestroyAllCreatures();
                    break;

                case EffectId.Spell_ChargeMana:
                    ChargeTopOfDeck(actor);
                    break;

                case EffectId.Spell_DiscardRandom:
                    DiscardRandom(OpponentOf(actor), Math.Max(0, effect.Value));
                    break;

                case EffectId.Spell_ReturnUpToToHand:
                {
                    for (var k = 0; k < Math.Max(1, effect.Value); k++)
                    {
                        var t = Next();
                        if (t is null)
                            break;
                        var target = ResolveSpellTarget(effect, actor, t.Value.Owner, t.Value.Index);
                        if (target is null)
                            continue;
                        ReturnToHand(target);
                    }
                    break;
                }

                case EffectId.Spell_DestroyPowerAtMost:
                case EffectId.Spell_ReturnToHand:
                case EffectId.Spell_TapCreature:
                case EffectId.Spell_UntapOwnCreature:
                case EffectId.Spell_BoostPower:
                {
                    var t = Next();
                    if (t is null)
                        break;
                    var target = ResolveSpellTarget(effect, actor, t.Value.Owner, t.Value.Index);
                    if (target is null)
                        break;
                    ApplyTargetedEffect(effect, target);
                    break;
                }
            }
        }
    }

    private CardInstance? ResolveSpellTarget(CardEffect effect, Player actor, Player targetOwner, int targetIndex)
    {
        if (!effect.NeedsTarget)
            return null;
        if (targetIndex < 0 || targetIndex >= targetOwner.BattleZone.Count)
            throw new RuleViolationException("The target index is out of range of the battle zone.");
        var target = targetOwner.BattleZone[targetIndex];
        if (!target.Card.IsCreature)
            throw new RuleViolationException($"'{target.Card.Name}' is not a creature and cannot be targeted.");

        switch (effect.Target)
        {
            case EffectTargetScope.OwnCreature:
                if (!ReferenceEquals(targetOwner, actor))
                    throw new RuleViolationException("You may only target one of your own creatures.");
                break;
            case EffectTargetScope.OpponentCreature:
                if (ReferenceEquals(targetOwner, actor))
                    throw new RuleViolationException("You may only target one of your opponent's creatures.");
                break;
        }

        if (effect.Id == EffectId.Spell_DestroyPowerAtMost && CurrentPower(target) > effect.Value)
            throw new RuleViolationException(
                $"'{target.Card.Name}' has power greater than {effect.Value} and cannot be destroyed.");
        return target;
    }

    /// <summary>True when this creature is a legal target for the card's first targeting effect (normal cast).</summary>
    public bool IsLegalSpellTarget(Card spell, Player targetOwner, int targetIndex)
        => IsLegalSpellTarget(spell, ActivePlayer, targetOwner, targetIndex);

    /// <summary>
    /// True when this creature is a legal target for the card's first targeting
    /// effect, relative to <paramref name="actor"/> (the player playing the card).
    /// </summary>
    public bool IsLegalSpellTarget(Card spell, Player actor, Player targetOwner, int targetIndex)
    {
        var effect = spell.Effects.FirstOrDefault(e => e.NeedsTarget);
        if (effect is null)
            return false;
        if (targetIndex < 0 || targetIndex >= targetOwner.BattleZone.Count)
            return false;
        var target = targetOwner.BattleZone[targetIndex];
        if (!target.Card.IsCreature)
            return false;
        if (effect.Id == EffectId.Spell_DestroyPowerAtMost && CurrentPower(target) > effect.Value)
            return false;
        return effect.Target switch
        {
            EffectTargetScope.OwnCreature => ReferenceEquals(targetOwner, actor),
            EffectTargetScope.OpponentCreature => ReferenceEquals(targetOwner, OpponentOf(actor)),
            _ => true,
        };
    }

    private void EnsureLegalTargets(Player actor, Card card, IReadOnlyList<SpellTarget>? targets)
    {
        if (targets is null)
            return;
        foreach (var t in targets)
        {
            if (!IsLegalSpellTarget(card, actor, t.Owner, t.Index))
                throw new RuleViolationException($"'{card.Name}' cannot target that creature.");
        }
    }

    private Player OpponentOf(Player p) => ReferenceEquals(p, Player1) ? Player2 : Player1;

    private void ApplyTargetedEffect(CardEffect effect, CardInstance target)
    {
        switch (effect.Id)
        {
            case EffectId.Spell_DestroyPowerAtMost:
            case EffectId.OnPlay_DestroyPowerAtMost:
                DestroyCreature(target);
                break;

            case EffectId.Spell_ReturnToHand:
            case EffectId.OnPlay_ReturnToHand:
                ReturnToHand(target);
                break;

            case EffectId.Spell_TapCreature:
            case EffectId.OnPlay_TapCreature:
                target.Tap();
                break;

            case EffectId.Spell_UntapOwnCreature:
            case EffectId.OnPlay_UntapOwnCreature:
                target.Untap();
                break;

            case EffectId.Spell_BoostPower:
                target.TempPower += effect.Value;
                break;
        }
    }

    private void ReturnToHand(CardInstance target)
    {
        var owner = target.Owner!;
        switch (target.Zone)
        {
            case Zone.BattleZone:
                owner.BattleZone.Remove(target);
                // Whatever was under the creature stays behind in the graveyard when the
                // top of an evolution stack is returned to hand.
                foreach (var under in target.Underneath)
                {
                    if (under.Owner is { } uo && under.Zone == Zone.Underneath)
                    {
                        under.Zone = Zone.Graveyard;
                        uo.Graveyard.Add(under);
                    }
                }
                target.Underneath.Clear();
                break;
            case Zone.ManaZone:
                owner.ManaZone.Remove(target);
                break;
            case Zone.Graveyard:
                owner.Graveyard.Remove(target);
                break;
        }
        target.Zone = Zone.Hand;
        target.IsTapped = false;
        target.IsSummoningSick = false;
        target.AttackedThisTurn = false;
        owner.Hand.Add(target);
    }

    private void DestroyAllCreatures()
    {
        var all = Player1.BattleZone.Concat(Player2.BattleZone).ToList();
        foreach (var c in all)
            DestroyCreature(c);
    }

    private void ChargeTopOfDeck(Player p)
    {
        if (p.Deck.Count == 0)
            return;
        var card = p.Deck[0];
        p.Deck.RemoveAt(0);
        p.ManaZone.Add(new CardInstance(card, p) { Zone = Zone.ManaZone });
    }

    /// <summary>Move up to <paramref name="count"/> cards from the player's graveyard
    /// into their mana zone (Bliss Totem), taking the most recently added cards.</summary>
    private void MoveGraveToMana(Player p, int count)
    {
        for (var i = 0; i < count && p.Graveyard.Count > 0; i++)
        {
            var card = p.Graveyard[^1];
            p.Graveyard.RemoveAt(p.Graveyard.Count - 1);
            card.Zone = Zone.ManaZone;
            p.ManaZone.Add(card);
        }
    }

    /// <summary>Move up to <paramref name="count"/> cards from the player's hand into
    /// their mana zone (Tangle Fist), taking cards from the top of the hand.</summary>
    private void MoveHandToMana(Player p, int count)
    {
        for (var i = 0; i < count && p.Hand.Count > 0; i++)
        {
            var card = p.Hand[0];
            p.Hand.RemoveAt(0);
            card.Zone = Zone.ManaZone;
            p.ManaZone.Add(card);
        }
    }

    /// <summary>Each player puts one card from their mana zone into their graveyard
    /// (Sky Crusher); the top of the zone is sacrificed.</summary>
    private void MoveManaToGrave(Player p)
    {
        if (p.ManaZone.Count == 0)
            return;
        var card = p.ManaZone[0];
        p.ManaZone.RemoveAt(0);
        card.Zone = Zone.Graveyard;
        p.Graveyard.Add(card);
    }

    private void DiscardRandom(Player p, int count)
    {
        for (var i = 0; i < count && p.Hand.Count > 0; i++)
        {
            var index = Rng.Next(p.Hand.Count);
            var card = p.Hand[index];
            p.Hand.RemoveAt(index);
            card.Zone = Zone.Graveyard;
            p.Graveyard.Add(card);
        }
    }

    /// <summary>The static (non-attack-time) power of a creature, including auras and continuous abilities.</summary>
    private int CurrentPower(CardInstance c)
    {
        var power = c.Card.Power;

        // Auras: every other creature's "each other {race} gets +{N}" ability.
        foreach (var zone in new[] { Player1.BattleZone, Player2.BattleZone })
        {
            foreach (var other in zone)
            {
                if (ReferenceEquals(other, c))
                    continue;
                foreach (var e in other.Card.Effects)
                {
                    if (e.Id == EffectId.StaticPower_AuraRace && RaceMatches(c.Card.Race, e.Data))
                        power += e.Value;
                }
            }
        }

        // Self static continuous effects.
        foreach (var eff in c.Card.Effects)
        {
            switch (eff.Id)
            {
                case EffectId.StaticPower_AlwaysPerOtherCreature:
                    power += CountOtherOwnCreatures(c, eff) * eff.Value;
                    break;
                case EffectId.StaticPower_AlwaysWhileHaveRace:
                    if (HasOwnCreatureOfRace(c, eff.Data))
                        power += eff.Value;
                    break;
            }
        }

        return power + c.TempPower;
    }

    /// <summary>Extra power granted only while attacking (Power Attacker and attack-time effects).</summary>
    private int AttackPowerBoost(CardInstance instance)
    {
        var sum = 0;
        foreach (var e in instance.Card.Effects)
        {
            switch (e.Id)
            {
                case EffectId.PowerAttacker_AttackBoost:
                    sum += e.Value;
                    break;

                case EffectId.StaticPower_AttackPerGraveyardCiv:
                {
                    if (instance.Owner is not { } owner
                        || !Enum.TryParse<Civilization>(e.Data, true, out var civ))
                        break;
                    var count = owner.Graveyard.Count(g => g.Card.Civilization == civ);
                    sum += count * e.Value;
                    break;
                }

                case EffectId.StaticPower_AttackPerOtherCreature:
                    sum += CountOtherOwnCreatures(instance, e) * e.Value;
                    break;

                case EffectId.StaticPower_AttackWhileHaveRace:
                    if (HasOwnCreatureOfRace(instance, e.Data))
                        sum += e.Value;
                    break;
            }
        }
        return sum;
    }

    /// <summary>Other creatures in the attacker's battle zone, optionally filtered by race.</summary>
    private static int CountOtherOwnCreatures(CardInstance c, CardEffect effect)
    {
        var owner = c.Owner;
        if (owner is null)
            return 0;
        var count = 0;
        foreach (var other in owner.BattleZone)
        {
            if (ReferenceEquals(other, c))
                continue;
            if (string.IsNullOrWhiteSpace(effect.Data) || RaceMatches(other.Card.Race, effect.Data))
                count++;
        }
        return count;
    }

    /// <summary>True when the creature's owner has a creature with the given race in the battle zone.</summary>
    private static bool HasOwnCreatureOfRace(CardInstance c, string data)
    {
        var owner = c.Owner;
        if (owner is null || string.IsNullOrWhiteSpace(data))
            return false;
        return owner.BattleZone.Any(x => RaceMatches(x.Card.Race, data));
    }

    private static bool RaceMatches(string race, string data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return true;
        if (string.IsNullOrWhiteSpace(race))
            return false;
        return race.Contains(data, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CivMatches(Civilization civ, string data)
    {
        return string.IsNullOrWhiteSpace(data)
            || (Enum.TryParse<Civilization>(data, true, out var parsed) && parsed == civ);
    }

    private static bool RaceEquals(string race, string? data) =>
        string.Equals(race, data, StringComparison.OrdinalIgnoreCase);

    private static bool IsRaceChoosingEffect(EffectId id) => id is
        EffectId.Tap_ChooseRaceUntapEot or
        EffectId.Tap_ChooseRaceGrantSlayerEot or
        EffectId.Tap_ChooseRaceToHandEot;

    /// <summary>
    /// The distinct creature races currently present in either battle zone - the
    /// legal choices for a tap ability that says "choose a race". Non-empty while
    /// the active player controls at least one creature (its own race is always a
    /// member of the pool).
    /// </summary>
    public IReadOnlyList<string> LegalRaceChoices(Player actor)
    {
        return Player1.BattleZone.Concat(Player2.BattleZone)
            .Select(c => c.Card.Race)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The ready, untriggered creatures of the active player that have "attacks each
    /// turn" and still have a legal attack this turn. The active player must attack
    /// with all of them before ending the Main phase.
    /// </summary>
    public List<CardInstance> MustAttackList()
    {
        var result = new List<CardInstance>();
        var active = ActivePlayer;
        var defender = Opponent;
        foreach (var c in active.BattleZone)
        {
            if (!c.Card.HasKeyword(Keyword.AttacksEachTurn))
                continue;
            if (c.IsTapped || c.IsSummoningSick || c.AttackedThisTurn)
                continue;
            // "Can't attack while the opponent has more creatures" blocks every
            // attack form, so it also cancels the must-attack obligation.
            var outnumbered = defender.BattleZone.Count > active.BattleZone.Count;
            if (outnumbered && c.HasKeywordNow(Keyword.CannotAttackOutnumbered))
                continue;
            // With no shields left a direct attack wins the game, so it is always a
            // legal (in fact the winning) way to satisfy the must-attack obligation.
            var canAttackPlayer = !c.HasKeywordNow(Keyword.CannotAttackPlayers);
            var canAttackCreature = !c.HasKeywordNow(Keyword.CannotAttackCreatures)
                && defender.BattleZone.Any(t =>
                    t.Card.IsCreature
                    && !t.HasKeywordNow(Keyword.CannotBeAttacked)
                    && (t.IsTapped || c.HasKeywordNow(Keyword.CanAttackUntappedCreatures)));
            if (canAttackPlayer || canAttackCreature)
                result.Add(c);
        }
        return result;
    }

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

    private static void RequireHandCard(Player player, int index)
    {
        if (index < 0 || index >= player.Hand.Count)
            throw new RuleViolationException("The hand index is out of range.");
    }

    private static CardInstance TakeFromHand(List<CardInstance> hand, int index)
    {
        var c = hand[index];
        hand.RemoveAt(index);
        return c;
    }

    private void EnsureMain() => EnsureTurnPhase(GamePhase.Main);

    private void EnsureTriggerWindowClosed()
    {
        if (ShieldTriggerWindowActive)
            throw new RuleViolationException("Resolve the pending Shield Trigger cards (or decline them) before taking another action.");
    }

    private void EnsureTurnPhase(GamePhase required)
    {
        if (Phase != required)
            throw new RuleViolationException($"This action is only allowed during {required}. Current phase: {phaseName(Phase)}.");
        if (IsGameOver)
            throw new RuleViolationException("The game is already over.");
    }

    private static string phaseName(GamePhase phase) => phase.ToString();

    private void EnsureNotOver()
    {
        if (IsGameOver)
            throw new RuleViolationException("The game is already over.");
    }
}