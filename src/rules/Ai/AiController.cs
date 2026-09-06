using System;
using System.Collections.Generic;
using System.Linq;

namespace DuelMasters.Domain.Ai;

/// <summary>How one AI decision step resolved.</summary>
public enum AiStepKind
{
    /// <summary>A mana charge, summon, cast or attack was applied to the game.</summary>
    ActionTaken,
    /// <summary>The AI wants to attack the opponent directly but a creature with
    /// the Blocker keyword could intercept - the caller must resolve the
    /// defender's block choice (interactive UI) and then call <see cref="Step"/> again.</summary>
    NeedsBlockChoice,
    /// <summary>The AI broke shields and the DEFENDER (the other player) now owns a
    /// shield-trigger window. The attacker's turn is paused until that side resolves
    /// it; call <see cref="Step"/> again afterwards.</summary>
    WaitingOnShieldTriggers,
    /// <summary>The AI has no more actions; the caller should end its turn.</summary>
    TurnEnded,
}

/// <summary>The result of one <see cref="AiController.Step"/> call.</summary>
public readonly record struct AiStep(AiStepKind Kind, int AttackerIndex);

/// <summary>
/// A turn-taking opponent for the shared DuelMasters rules engine. Pure C# with
/// zero dependencies, like <see cref="DuelGame"/> itself - the local Arena and the
/// authoritative backend can both pilot an AI match with it.
///
/// The AI only ever calls the same public <see cref="DuelGame"/> APIs the UI uses,
/// so any decision it makes is bound by the real rule set (mana affinity, attack
/// lock, summoning sickness, blocking, deck-out, ...). Heuristics are intentionally
/// simple and profile-tuned.
/// </summary>
public sealed class AiController
{
    public AiController(Player self, AiProfile? profile = null)
    {
        Self = self ?? throw new ArgumentNullException(nameof(self));
        Profile = profile ?? AiProfile.Standard;
    }

    /// <summary>The player this controller pilots.</summary>
    public Player Self { get; }

    public AiProfile Profile { get; }

    /// <summary>
    /// Perform exactly one autonomous action for the current Main phase and return
    /// how it resolved. Call in a loop until <see cref="AiStepKind.TurnEnded"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called outside the AI's Main phase.</exception>
    public AiStep Step(DuelGame game)
    {
        if (!ReferenceEquals(game.ActivePlayer, Self))
            throw new InvalidOperationException("AI step called outside its own turn.");
        if (game.Phase != GamePhase.Main)
            throw new InvalidOperationException($"AI step called during {game.Phase}, expected {GamePhase.Main}.");

        // A shield-trigger window interrupts the attacker's turn. Resolve the AI's
        // own windows here; otherwise report the pause to the caller.
        if (game.ShieldTriggerWindowActive)
        {
            if (ReferenceEquals(game.ShieldTriggerOwner, Self))
                ResolveShieldTriggers(game);
            else
                return new AiStep(AiStepKind.WaitingOnShieldTriggers, -1);
        }

        // 1) Charge one mana card if the turn allows it. A smart charge keeps the
        //    turn's best plays playable and dumps dead / duplicate / uncastable cards.
        if (!game.ManaChargedThisTurn && TryChooseManaCharge(out var manaIndex))
        {
            game.PlayManaToManaZone(manaIndex);
            return new AiStep(AiStepKind.ActionTaken, -1);
        }

        // 2) Develop the battlefield before attacking (attacks lock summons/casts).
        if (!game.HasAttackedThisTurn && TryChoosePlay(game, out var playIndex, out var spellOwner, out var spellTarget))
        {
            var card = Self.Hand[playIndex].Card;
            if (card.IsCreature)
                game.SummonCreature(playIndex);
            else if (spellOwner is not null)
                game.CastSpell(playIndex, spellOwner, spellTarget);
            else
                game.CastSpell(playIndex);
            return new AiStep(AiStepKind.ActionTaken, -1);
        }

        // 3) Attack with a ready creature.
        if (TryChooseAttack(game, out var attackerIndex, out var needsBlockChoice))
        {
            if (needsBlockChoice)
                return new AiStep(AiStepKind.NeedsBlockChoice, attackerIndex);
            if (game.ShieldTriggerWindowActive)
            {
                // The hit broke shields: resolve our own triggers right here, or pause
                // the turn so the defender (interactive UI or the other AI) can decide.
                if (ReferenceEquals(game.ShieldTriggerOwner, Self))
                    ResolveShieldTriggers(game);
                else
                    return new AiStep(AiStepKind.WaitingOnShieldTriggers, -1);
            }
            return new AiStep(AiStepKind.ActionTaken, -1);
        }

        return new AiStep(AiStepKind.TurnEnded, -1);
    }

    /// <summary>
    /// Convenience driver for tests / zero-UI hosts: plays the AI's full turn
    /// (mana, plays, attacks, end). When a Blocker could intercept, the AI decides
    /// for itself via <see cref="DecideBlock"/>. If the AI attacks and the DEFENDER
    /// ends up owning a shield-trigger window, the turn stops there so the caller
    /// can resolve it (a later <see cref="PlayTurn"/> call resumes the attacks).
    /// </summary>
    public void PlayTurn(DuelGame game)
    {
        var steps = 0;
        while (!game.IsGameOver && game.Phase == GamePhase.Main && steps++ < 200)
        {
            if (game.ShieldTriggerWindowActive)
            {
                if (ReferenceEquals(game.ShieldTriggerOwner, Self))
                    ResolveShieldTriggers(game);
                else
                    break; // the defender must resolve before this turn can continue
                continue;
            }

            var step = Step(game);
            if (step.Kind is AiStepKind.TurnEnded or AiStepKind.WaitingOnShieldTriggers)
                break;
            if (step.Kind != AiStepKind.NeedsBlockChoice)
                continue;

            if (DecideBlock(game, step.AttackerIndex, out var blockerIndex))
                game.AttackPlayer(step.AttackerIndex, Self, blockerIndex);
            else
                game.AttackPlayer(step.AttackerIndex);
        }

        if (!game.IsGameOver && game.Phase == GamePhase.Main && !game.ShieldTriggerWindowActive)
        {
            game.EndMainPhase();
            game.EndTurn();
        }
    }

    /// <summary>
    /// The AI, as defender, picks whether to intercept a direct attack with a
    /// Blocker. It blocks when the blocker survives or trades favourably, or as a
    /// last resort when the profile is brave and shields are almost gone.
    /// </summary>
    public bool DecideBlock(DuelGame game, int attackerIndex, out int blockerIndex)
    {
        blockerIndex = -1;
        if (!ReferenceEquals(game.Opponent, Self))
            throw new InvalidOperationException("DecideBlock is only meaningful when the AI is the defender.");

        var attacker = game.ActivePlayer.BattleZone.ElementAtOrDefault(attackerIndex);
        if (attacker is null || attacker.IsTapped)
            return false;

        var blockers = Self.BattleZone
            .Select((c, i) => (Instance: c, Index: i))
            .Where(x => x.Instance.Card.IsCreature
                && x.Instance.Card.HasKeyword(Keyword.Blocker)
                && !x.Instance.IsTapped)
            .OrderByDescending(x => x.Instance.Card.Power)
            .ToList();
        if (blockers.Count == 0)
            return false;

        var attackerPower = attacker.Card.Power;
        foreach (var blocker in blockers)
        {
            var power = blocker.Instance.Card.Power;
            if (power > attackerPower)
            {
                blockerIndex = blocker.Index;
                return true;
            }
            if (power == attackerPower && Profile.BlockCourage >= 0.5f)
            {
                blockerIndex = blocker.Index;
                return true;
            }
        }

        if (Self.ShieldCount <= 1 && Profile.BlockCourage >= 0.8f)
        {
            blockerIndex = blockers[0].Index;
            return true;
        }

        return false;
    }

    // ---------------------------------------------------------- heuristics

    /// <summary>Pick the least useful hand card to charge as mana.</summary>
    private bool TryChooseManaCharge(out int index)
    {
        var hand = Self.Hand;
        var openMana = Self.ManaZone.Count(m => !m.IsTapped);
        var bestScore = float.NegativeInfinity;
        index = -1;

        for (var i = 0; i < hand.Count; i++)
        {
            var card = hand[i].Card;
            var score = 0f;

            var copies = hand.Count(h => h.Card.Name == card.Name);
            if (copies > 1)
                score += 3f;                      // duplicates are safe to dump
            if (card.ManaCost > openMana)
                score += 2f;                      // uncastable this turn - fine as mana
            if (card.IsCreature && card.Power >= 5000)
                score -= 3f;                      // keep finishers
            else if (card.IsCreature && card.ManaCost <= openMana)
                score -= 1.5f;                    // keep creatures we can play now
            else if (card.CardType == CardType.Spell && card.ManaCost <= openMana)
                score -= 0.5f;                    // playable spells are mildly worth keeping

            if (score > bestScore)
            {
                bestScore = score;
                index = i;
            }
        }

        return index >= 0;
    }

    /// <summary>
    /// Pick the best playable card. Creatures are preferred; spells are only played
    /// when they carry a real effect (removal, tap, untap, boost, draw) and the mana
    /// budget survives it. For a targeted spell, <paramref name="spellOwner"/> /
    /// <paramref name="spellTarget"/> give the chosen legal target (null/-1 otherwise).
    /// </summary>
    private bool TryChoosePlay(DuelGame game, out int playIndex, out Player? spellOwner, out int spellTarget)
    {
        playIndex = -1;
        spellOwner = null;
        spellTarget = -1;

        var bestScore = float.NegativeInfinity;
        for (var i = 0; i < Self.Hand.Count; i++)
        {
            var card = Self.Hand[i].Card;
            if (!card.IsCreature || !game.CanPlay(Self, card))
                continue;

            var score = card.ManaCost * Profile.ValueTempo + (card.Power / 1000f) * (1f - Profile.ValueTempo);
            if (card.HasKeyword(Keyword.Blocker))
                score += 2f;
            if (card.Effects.Any(e => e.Id == EffectId.OnPlay_Draw))
                score += 2.5f;
            if (card.Effects.Any(e => e.Id == EffectId.OnDestroyed_Draw))
                score += 1f;
            if (score > bestScore)
            {
                bestScore = score;
                playIndex = i;
            }
        }
        if (playIndex >= 0)
            return true;

        for (var i = 0; i < Self.Hand.Count; i++)
        {
            var card = Self.Hand[i].Card;
            if (card.CardType != CardType.Spell || !game.CanPlay(Self, card))
                continue;
            if (TryChooseSpellPlay(game, i, out var owner, out var target))
            {
                playIndex = i;
                spellOwner = owner;
                spellTarget = target;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Decide whether to cast spell <paramref name="handIndex"/> this turn and, for
    /// targeted spells, which legal target to hit. Removal denies the opponent's
    /// biggest creature within the power cap; tap/untap/boost enable tempo; draw
    /// spells burn spare mana only.
    /// </summary>
    private bool TryChooseSpellPlay(DuelGame game, int handIndex, out Player? targetOwner, out int targetIndex)
    {
        targetOwner = null;
        targetIndex = -1;
        var card = Self.Hand[handIndex].Card;
        var openMana = Self.ManaZone.Count(m => !m.IsTapped);
        var surplus = openMana - card.ManaCost;

        var needsTarget = card.Effects.Any(e => e.NeedsTarget);
        if (needsTarget)
        {
            if (!TryChooseSpellTarget(game, card, out var owner, out var index))
                return false;
            targetOwner = owner;
            targetIndex = index;

            if (!ReferenceEquals(owner, Self))
            {
                // Cards aimed at the opponent: removal/tap are combat pressure.
                var foePower = owner.BattleZone[index].Card.Power / 1000f;
                var isRemoval = card.Effects.Any(e =>
                    e.Id is EffectId.Spell_DestroyPowerAtMost or EffectId.Spell_ReturnToHand);
                var isTap = card.Effects.Any(e => e.Id == EffectId.Spell_TapCreature);
                if (isRemoval && (surplus >= 1 || foePower >= 3f))
                    return true;
                if (isTap && surplus >= 1)
                    return true;
                return false;
            }

            // Self-targeted (untap / boost): only with spare mana.
            return surplus >= 1;
        }

        // No-target spells (draw, ...): worth it when mana is spare.
        return surplus >= 1;
    }

    /// <summary>
    /// Pick the best legal target for <paramref name="spell"/> among both battle
    /// zones, scored per effect (destroy/return want the strongest foe creature
    /// within range, tap wants a ready threat, untap/boost aim at own battle zone).
    /// The engine's own <see cref="DuelGame.IsLegalSpellTarget"/> keeps every choice
    /// rule-legal, so the AI can never target illegally.
    /// </summary>
    private bool TryChooseSpellTarget(DuelGame game, Card spell, out Player targetOwner, out int targetIndex)
    {
        targetOwner = Self;
        targetIndex = -1;
        var bestValue = float.NegativeInfinity;
        var bestOwner = Self;
        var bestIndex = -1;
        // During a normal Main phase the AI is the active player (foe = Opponent);
        // while resolving our own shield triggers the ATTACKER is the foe instead.
        var foe = ReferenceEquals(game.ActivePlayer, Self) ? game.Opponent : game.ActivePlayer;

        void Consider(Player owner, int index, float value)
        {
            if (value > bestValue)
            {
                bestValue = value;
                bestOwner = owner;
                bestIndex = index;
            }
        }

        foreach (var effect in spell.Effects.Where(e => e.NeedsTarget))
        {
            switch (effect.Id)
            {
                case EffectId.Spell_DestroyPowerAtMost:
                    for (var i = 0; i < foe.BattleZone.Count; i++)
                    {
                        var c = foe.BattleZone[i];
                        if (game.IsLegalSpellTarget(spell, Self, foe, i))
                            Consider(foe, i, 1.5f + c.Card.Power / 1000f + (c.IsTapped ? 0f : 0.5f));
                    }
                    break;

                case EffectId.Spell_ReturnToHand:
                    for (var i = 0; i < foe.BattleZone.Count; i++)
                    {
                        var c = foe.BattleZone[i];
                        if (game.IsLegalSpellTarget(spell, Self, foe, i))
                            Consider(foe, i, 1f + c.Card.Power / 1000f + (c.IsTapped ? 0f : 0.5f));
                    }
                    break;

                case EffectId.Spell_TapCreature:
                    for (var i = 0; i < foe.BattleZone.Count; i++)
                    {
                        var c = foe.BattleZone[i];
                        if (game.IsLegalSpellTarget(spell, Self, foe, i) && !c.IsTapped)
                            Consider(foe, i, 0.75f + c.Card.Power / 2000f);
                    }
                    break;

                case EffectId.Spell_UntapOwnCreature:
                    for (var i = 0; i < Self.BattleZone.Count; i++)
                    {
                        var c = Self.BattleZone[i];
                        if (game.IsLegalSpellTarget(spell, Self, Self, i) && c.IsTapped)
                            Consider(Self, i, 0.5f + c.Card.Power / 2000f);
                    }
                    break;

                case EffectId.Spell_BoostPower:
                    for (var i = 0; i < Self.BattleZone.Count; i++)
                    {
                        var c = Self.BattleZone[i];
                        if (game.IsLegalSpellTarget(spell, Self, Self, i) && !c.IsTapped)
                            Consider(Self, i, 0.4f + c.Card.Power / 5000f);
                    }
                    break;
            }
        }

        if (bestIndex < 0)
            return false;
        targetOwner = bestOwner;
        targetIndex = bestIndex;
        return true;
    }

    /// <summary>
    /// The AI, as DEFENDER, resolves an open shield-trigger window it owns: plays
    /// every pending card that is useful (creatures are always free value, spells
    /// only when they have a legal target / a real effect) and declines the rest.
    /// </summary>
    public void ResolveShieldTriggers(DuelGame game)
    {
        if (!game.ShieldTriggerWindowActive)
            return;
        if (!ReferenceEquals(game.ShieldTriggerOwner, Self))
            throw new InvalidOperationException("ResolveShieldTriggers requires the AI to own the open shield-trigger window.");

        foreach (var instance in game.PendingShieldTriggers.ToList())
        {
            if (!game.ShieldTriggerWindowActive)
                break;
            var handIndex = Self.Hand.IndexOf(instance);
            if (handIndex < 0)
                continue;
            var card = instance.Card;

            if (card.IsCreature)
            {
                game.PlayShieldTrigger(handIndex);
                continue;
            }
            if (card.Effects.All(e => !e.NeedsTarget) && card.Effects.Any())
            {
                game.PlayShieldTrigger(handIndex);
                continue;
            }
            if (TryChooseSpellTarget(game, card, out var owner, out var index))
                game.PlayShieldTrigger(handIndex, owner, index);
            // No legal target -> the card is left in hand (declined below).
        }

        if (game.ShieldTriggerWindowActive)
            game.DeclineShieldTriggers();
    }

    /// <summary>
    /// Pick one attack. Prefers killing tapped creatures it can overpower for free,
    /// then swings for shields. Returns <c>true</c> and applies the attack (creature
    /// or direct) unless a Blocker could intercept a direct swing, in which case
    /// <paramref name="needsBlockChoice"/> is set and nothing is applied.
    /// </summary>
    private bool TryChooseAttack(DuelGame game, out int attackerIndex, out bool needsBlockChoice)
    {
        attackerIndex = -1;
        needsBlockChoice = false;

        var ready = Self.BattleZone
            .Select((c, i) => (Instance: c, Index: i))
            .Where(x => !x.Instance.IsTapped
                && !x.Instance.IsSummoningSick
                && x.Instance.Card.IsCreature)
            .OrderByDescending(x => x.Instance.Card.Power)
            .ToList();
        if (ready.Count == 0)
            return false;

        // Free favourable kills first: out-power a tapped creature, nobody dies.
        var foe = game.Opponent;
        for (var r = 0; r < ready.Count; r++)
        {
            var (attacker, aIdx) = ready[r];
            var target = foe.BattleZone
                .Select((c, i) => (Instance: c, Index: i))
                .Where(x => x.Instance.IsTapped && attacker.Card.Power > x.Instance.Card.Power)
                .OrderByDescending(x => x.Instance.Card.Power)
                .FirstOrDefault();
            if (target.Instance is not null)
            {
                game.AttackCreature(aIdx, target.Index);
                attackerIndex = aIdx;
                return true;
            }
        }

        // Otherwise crash the shields. Only a potential Blocker forces the caller
        // to resolve the defender's interception; with the catalog as it stands
        // (no Blocker data yet) this behaves as a direct swing.
        var strongest = ready[0];
        if (Profile.Aggression >= 0.15f)
        {
            var canBlock = foe.BattleZone.Any(c =>
                c.Card.IsCreature && !c.IsTapped && c.Card.HasKeyword(Keyword.Blocker));
            if (canBlock)
            {
                attackerIndex = strongest.Index;
                needsBlockChoice = true;
                return true;
            }
            game.AttackPlayer(strongest.Index);
            attackerIndex = strongest.Index;
            return true;
        }

        return false;
    }
}