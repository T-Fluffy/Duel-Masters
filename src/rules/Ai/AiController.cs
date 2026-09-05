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

        // 1) Charge one mana card if the turn allows it. A smart charge keeps the
        //    turn's best plays playable and dumps dead / duplicate / uncastable cards.
        if (!game.ManaChargedThisTurn && TryChooseManaCharge(out var manaIndex))
        {
            game.PlayManaToManaZone(manaIndex);
            return new AiStep(AiStepKind.ActionTaken, -1);
        }

        // 2) Develop the battlefield before attacking (attacks lock summons/casts).
        if (!game.HasAttackedThisTurn && TryChoosePlay(game, out var playIndex))
        {
            var card = Self.Hand[playIndex].Card;
            if (card.IsCreature)
                game.SummonCreature(playIndex);
            else
                game.CastSpell(playIndex);
            return new AiStep(AiStepKind.ActionTaken, -1);
        }

        // 3) Attack with a ready creature.
        if (TryChooseAttack(game, out var attackerIndex, out var needsBlockChoice))
        {
            return needsBlockChoice
                ? new AiStep(AiStepKind.NeedsBlockChoice, attackerIndex)
                : new AiStep(AiStepKind.ActionTaken, -1);
        }

        return new AiStep(AiStepKind.TurnEnded, -1);
    }

    /// <summary>
    /// Convenience driver for tests / zero-UI hosts: plays the AI's full turn
    /// (mana, plays, attacks, end). When a Blocker could intercept, the AI decides
    /// for itself via <see cref="DecideBlock"/>.
    /// </summary>
    public void PlayTurn(DuelGame game)
    {
        var steps = 0;
        while (!game.IsGameOver && game.Phase == GamePhase.Main && steps++ < 200)
        {
            var step = Step(game);
            if (step.Kind == AiStepKind.TurnEnded)
                break;
            if (step.Kind != AiStepKind.NeedsBlockChoice)
                continue;

            if (DecideBlock(game, step.AttackerIndex, out var blockerIndex))
                game.AttackPlayer(step.AttackerIndex, Self, blockerIndex);
            else
                game.AttackPlayer(step.AttackerIndex);
        }

        if (!game.IsGameOver && game.Phase == GamePhase.Main)
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

    /// <summary>Pick the best playable card: creatures preferred, spells only when idle mana is surplus.</summary>
    private bool TryChoosePlay(DuelGame game, out int index)
    {
        index = -1;

        var bestScore = float.NegativeInfinity;
        for (var i = 0; i < Self.Hand.Count; i++)
        {
            var card = Self.Hand[i].Card;
            if (!card.IsCreature || !game.CanPlay(Self, card))
                continue;

            var score = card.ManaCost * Profile.ValueTempo + (card.Power / 1000f) * (1f - Profile.ValueTempo);
            if (card.HasKeyword(Keyword.Blocker))
                score += 2f;
            if (score > bestScore)
            {
                bestScore = score;
                index = i;
            }
        }

        if (index >= 0)
            return true;

        // Spells resolve no effects in this milestone; only spend surplus mana on
        // them so the upcoming turn's summons are never jeopardised.
        var openMana = Self.ManaZone.Count(m => !m.IsTapped);
        for (var i = 0; i < Self.Hand.Count; i++)
        {
            var card = Self.Hand[i].Card;
            if (card.CardType != CardType.Spell || !game.CanPlay(Self, card))
                continue;
            if (openMana - card.ManaCost >= 2)
            {
                index = i;
                return true;
            }
        }

        return false;
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