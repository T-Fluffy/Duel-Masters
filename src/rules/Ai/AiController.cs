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
        if (!game.ManaChargedThisTurn && TryChooseManaCharge(game, out var manaIndex))
        {
            game.PlayManaToManaZone(manaIndex);
            return new AiStep(AiStepKind.ActionTaken, -1);
        }

        // 2) Develop the battlefield before attacking (attacks lock summons/casts).
        if (!game.HasAttackedThisTurn && TryChoosePlay(game, out var playIndex, out var targets, out var evolveBaseIndex))
        {
            var card = Self.Hand[playIndex].Card;
            if (card.IsEvolution)
            {
                if (targets is { Count: 1 } single)
                    game.EvolveCreature(playIndex, evolveBaseIndex, single[0].Owner, single[0].Index);
                else
                    game.EvolveCreature(playIndex, evolveBaseIndex);
            }
            else if (card.IsCreature)
            {
                if (targets is { Count: 1 } single)
                    game.SummonCreature(playIndex, single[0].Owner, single[0].Index);
                else
                    game.SummonCreature(playIndex);
            }
            else if (targets is { Count: > 0 })
                game.CastSpell(playIndex, targets);
            else
                game.CastSpell(playIndex);
            return new AiStep(AiStepKind.ActionTaken, -1);
        }

        // 3) Use tap abilities before attacking: deck keeps flowing (draw/charge),
        //    removal softens the foe, and grants set up the attack wave. The engine
        //    forbids using them after the first attack this turn.
        if (!game.HasAttackedThisTurn && TryChooseTapAbility(game, out var tapCreatureIndex, out var tapTargets))
        {
            game.ActivateTapAbility(tapCreatureIndex, Self, tapTargets);
            return new AiStep(AiStepKind.ActionTaken, -1);
        }

        // 4) Attack with a ready creature.
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
        if (attacker is null || attacker.IsTapped || !DuelGame.CanBeBlocked(attacker.Card))
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
    private bool TryChooseManaCharge(DuelGame game, out int index)
    {
        var hand = Self.Hand;
        var openMana = Self.ManaZone.Count(m => !m.IsTapped);
        var bestScore = float.NegativeInfinity;
        index = -1;

        for (var i = 0; i < hand.Count; i++)
        {
            var card = hand[i].Card;
            if (card.IsEvolution && game.CanEvolve(Self, card))
                continue; // an evolution that can be played onto a base now beats charging it
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
    /// when they carry a real effect (removal, tap, untap, boost, draw, ramp,
    /// wipe) and the mana budget survives it. Evolution creatures are free and rank
    /// highly when a matching-race base is on board -
    /// <paramref name="evolveBaseIndex"/> carries the chosen base ( -1 otherwise).
    /// For a targeted play, <paramref name="targets"/> carries the chosen legal
    /// targets (one for a creature's on-play ability, one per spell targeting
    /// effect - null when the play needs no choice).
    /// </summary>
    private bool TryChoosePlay(DuelGame game, out int playIndex, out IReadOnlyList<SpellTarget>? targets, out int evolveBaseIndex)
    {
        playIndex = -1;
        targets = null;
        evolveBaseIndex = -1;
        if (game.HasAttackedThisTurn)
            return false;

        var bestScore = float.NegativeInfinity;
        for (var i = 0; i < Self.Hand.Count; i++)
        {
            var card = Self.Hand[i].Card;
            if (card.IsEvolution)
            {
                if (!game.CanEvolve(Self, card) || !game.TryGetEvolutionBase(Self, card, out var baseIdx))
                    continue;

                // Free, keeps mana untouched: a pure upgrade of a similar body.
                var evoScore = (card.Power / 1000f) * (1f - Profile.ValueTempo) + 3f;
                if (card.HasKeyword(Keyword.Blocker))
                    evoScore += 2f;
                if (card.HasKeyword(Keyword.SpeedAttacker) || card.HasKeyword(Keyword.Slayer))
                    evoScore += 1f;
                if (card.Effects.Any(e => e.Id == EffectId.OnPlay_Draw))
                    evoScore += 2.5f;
                if (card.Effects.Any(e => e.Id == EffectId.OnPlay_ChargeMana))
                    evoScore += 2f;
                if (card.Effects.Any(e => e.Id == EffectId.OnDestroyed_Draw))
                    evoScore += 1f;

                IReadOnlyList<SpellTarget>? evoChoice = null;
                if (DuelGame.HasOnPlayTargetChoice(card))
                {
                    if (TryChooseOnPlayTarget(game, card, out var owner, out var target))
                    {
                        evoChoice = new[] { new SpellTarget(owner, target) };
                        evoScore += 1.5f;
                    }
                    else
                    {
                        evoScore -= 2f; // the trigger will fizzle; only worth it for the body
                    }
                }

                if (evoScore > bestScore)
                {
                    bestScore = evoScore;
                    playIndex = i;
                    targets = evoChoice;
                    evolveBaseIndex = baseIdx;
                }
                continue;
            }

            if (!card.IsCreature || !game.CanSummon(Self, card))
                continue;

            var score = card.ManaCost * Profile.ValueTempo + (card.Power / 1000f) * (1f - Profile.ValueTempo);
            if (card.HasKeyword(Keyword.Blocker))
                score += 2f;
            if (card.HasKeyword(Keyword.SpeedAttacker) || card.HasKeyword(Keyword.Slayer))
                score += 1f;
            if (card.Effects.Any(e => e.Id == EffectId.OnPlay_Draw))
                score += 2.5f;
            if (card.Effects.Any(e => e.Id == EffectId.OnPlay_ChargeMana))
                score += 2f;
            if (card.Effects.Any(e => e.Id == EffectId.OnDestroyed_Draw))
                score += 1f;

            IReadOnlyList<SpellTarget>? choice = null;
            if (DuelGame.HasOnPlayTargetChoice(card))
            {
                if (TryChooseOnPlayTarget(game, card, out var owner, out var target))
                {
                    choice = new[] { new SpellTarget(owner, target) };
                    score += 1.5f;
                }
                else
                {
                    score -= 2f; // the trigger will fizzle; only worth it for the body
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                playIndex = i;
                targets = choice;
            }
        }
        if (playIndex >= 0)
            return true;

        for (var i = 0; i < Self.Hand.Count; i++)
        {
            var card = Self.Hand[i].Card;
            if (card.CardType != CardType.Spell || !game.CanPlay(Self, card))
                continue;
            if (TryChooseSpellPlay(game, i, out var spellTargets))
            {
                playIndex = i;
                targets = spellTargets;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Decide whether to cast spell <paramref name="handIndex"/> this turn and, for
    /// targeted spells, which legal targets to hit. Removal denies the opponent's
    /// biggest creature within the power cap; tap/untap/boost enable tempo; draw and
    /// ramp spells burn surplus mana only; wipes and discard press when they matter.
    /// </summary>
private bool TryChooseSpellPlay(DuelGame game, int handIndex, out IReadOnlyList<SpellTarget>? targets)
    {
        targets = null;
        var card = Self.Hand[handIndex].Card;
        var openMana = Self.ManaZone.Count(m => !m.IsTapped);
        var surplus = openMana - card.ManaCost;
        if (surplus < 0)
            return false;

        if (card.Effects.Any(e => e.NeedsTarget))
        {
            if (!TryChooseSpellTargets(game, card, out var list))
                return false;
            targets = list;

            var aimedAtFoe = list.Any(t => !ReferenceEquals(t.Owner, Self));
            var isRemoval = card.Effects.Any(e =>
                e.Id is EffectId.Spell_DestroyPowerAtMost or EffectId.Spell_ReturnToHand);
            var isTap = card.Effects.Any(e => e.Id == EffectId.Spell_TapCreature);
            if (aimedAtFoe)
            {
                var maxFoePower = list
                    .Where(t => !ReferenceEquals(t.Owner, Self))
                    .Max(t => (float)t.Owner.BattleZone[t.Index].Card.Power / 1000f);
                if (isRemoval && (surplus >= 1 || maxFoePower >= 3f))
                    return true;
                if (isTap && surplus >= 1)
                    return true;
                if (card.Effects.Any(e => e.Id == EffectId.Spell_ReturnUpToToHand) && surplus >= 0)
                    return true;
                return false;
            }

            return surplus >= 0; // self-targeted untap / boost
        }

        // Untargeted spells: mana ramp is always welcome, draw wants surplus mana,
        // board wipes only when the opponent boards up, discard when the foe holds.
        if (card.Effects.Any(e => e.Id == EffectId.Spell_ChargeMana))
            return true;
        if (card.Effects.Any(e => e.Id == EffectId.Spell_Draw))
            return surplus >= 1;
        if (card.Effects.Any(e => e.Id == EffectId.Spell_DestroyAllCreatures))
            return game.Opponent.BattleZone.Any(c => c.Card.IsCreature);
        if (card.Effects.Any(e => e.Id == EffectId.Spell_DiscardRandom))
            return game.Opponent.Hand.Count >= 2 && surplus >= 0;
        return true;
    }

    /// <summary>
    /// Build the target list for <paramref name="spell"/>: one legal target per
    /// targeting effect, in the order the engine resolves them ("return up to N"
    /// consumes up to N targets). Any targeting effect without a legal target
    /// rejects the spell so the AI never casts a wasted card.
    /// </summary>
    private bool TryChooseSpellTargets(DuelGame game, Card spell, out IReadOnlyList<SpellTarget> targets)
    {
        var result = new List<SpellTarget>();
        targets = result;
        // During a normal Main phase the AI is the active player (foe = Opponent);
        // while resolving our own shield triggers the ATTACKER is the foe instead.
        var foe = ReferenceEquals(game.ActivePlayer, Self) ? game.Opponent : game.ActivePlayer;

        foreach (var effect in spell.Effects.Where(e => e.NeedsTarget))
        {
            var bestValue = float.NegativeInfinity;
            Player? bestOwner = null;
            var bestIndex = -1;

            void Consider(Player owner, int index, float value)
            {
                if (value > bestValue)
                {
                    bestValue = value;
                    bestOwner = owner;
                    bestIndex = index;
                }
            }

            if (effect.Id == EffectId.Spell_ReturnUpToToHand)
            {
                var picks = new List<(Player Owner, int Index, float Value)>();
                for (var i = 0; i < foe.BattleZone.Count; i++)
                {
                    var c = foe.BattleZone[i];
                    if (c.Card.IsCreature && game.IsLegalSpellTarget(spell, Self, foe, i))
                        picks.Add((foe, i, 1f + c.Card.Power / 1000f + (c.IsTapped ? 0f : 0.5f)));
                }
                for (var i = 0; i < Self.BattleZone.Count; i++)
                {
                    var c = Self.BattleZone[i];
                    if (c.Card.IsCreature && game.IsLegalSpellTarget(spell, Self, Self, i))
                        picks.Add((Self, i, c.Card.Power / 3000f)); // bouncing own is a last resort
                }
                if (picks.Count == 0)
                    return false;
                foreach (var pick in picks.OrderByDescending(x => x.Value).Take(Math.Max(1, effect.Value)))
                    result.Add(new SpellTarget(pick.Owner, pick.Index));
                continue;
            }

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

            if (bestIndex < 0)
                return false;
            result.Add(new SpellTarget(bestOwner!, bestIndex));
        }

        targets = result;
        return true;
    }

    /// <summary>
    /// Pick the best legal creature for <paramref name="creature"/>'s on-play
    /// targeting ability: destroy/return want the strongest foe within the power
    /// cap, tap a ready enemy threat, untap a valuable own creature.
    /// </summary>
    private bool TryChooseOnPlayTarget(DuelGame game, Card creature, out Player targetOwner, out int targetIndex)
    {
        targetOwner = Self;
        targetIndex = -1;
        var foe = game.Opponent;
        var bestValue = float.NegativeInfinity;
        Player? bestOwner = null;
        var bestIndex = -1;

        void Consider(Player owner, int index, float value)
        {
            if (value > bestValue)
            {
                bestValue = value;
                bestOwner = owner;
                bestIndex = index;
            }
        }

        foreach (var effect in creature.Effects.Where(e => e.Id is
            EffectId.OnPlay_TapCreature or EffectId.OnPlay_ReturnToHand or
            EffectId.OnPlay_DestroyPowerAtMost or EffectId.OnPlay_UntapOwnCreature))
        {
            switch (effect.Id)
            {
                case EffectId.OnPlay_DestroyPowerAtMost:
                    for (var i = 0; i < foe.BattleZone.Count; i++)
                    {
                        var c = foe.BattleZone[i];
                        if (game.IsLegalOnPlayTarget(creature, Self, foe, i))
                            Consider(foe, i, 1.5f + c.Card.Power / 1000f + (c.IsTapped ? 0f : 0.5f));
                    }
                    break;

                case EffectId.OnPlay_ReturnToHand:
                    for (var i = 0; i < foe.BattleZone.Count; i++)
                    {
                        var c = foe.BattleZone[i];
                        if (game.IsLegalOnPlayTarget(creature, Self, foe, i))
                            Consider(foe, i, 1f + c.Card.Power / 1000f + (c.IsTapped ? 0f : 0.5f));
                    }
                    break;

                case EffectId.OnPlay_TapCreature:
                    for (var i = 0; i < foe.BattleZone.Count; i++)
                    {
                        var c = foe.BattleZone[i];
                        if (game.IsLegalOnPlayTarget(creature, Self, foe, i) && !c.IsTapped)
                            Consider(foe, i, 0.75f + c.Card.Power / 2000f);
                    }
                    break;

                case EffectId.OnPlay_UntapOwnCreature:
                    for (var i = 0; i < Self.BattleZone.Count; i++)
                    {
                        var c = Self.BattleZone[i];
                        if (game.IsLegalOnPlayTarget(creature, Self, Self, i) && c.IsTapped)
                            Consider(Self, i, 0.5f + c.Card.Power / 2000f);
                    }
                    break;
            }
        }

        if (bestIndex < 0)
            return false;
        targetOwner = bestOwner!;
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
                if (card.IsEvolution)
                    continue; // evolution creatures are only played by evolving onto a base
                game.PlayShieldTrigger(handIndex);
                continue;
            }
            if (TryChooseSpellTargets(game, card, out var targets))
                game.PlayShieldTrigger(handIndex, targets);
            else if (card.Effects.Any())
                game.PlayShieldTrigger(handIndex);
            // No legal target for a targeted spell -> the card is left in hand
            // (declined below); untargeted spells are played unconditionally.
        }

        if (game.ShieldTriggerWindowActive)
            game.DeclineShieldTriggers();
    }

    /// <summary>
    /// Pick a tap-ability creature to activate this turn, if any activation is
    /// useful. Global abilities (draw, charge, discard, civ-wide grants) are valued
    /// directly; targeted ones aim at the best legal card from the engine's own
    /// target pool. Never touches <see cref="EffectId.Tap_NotModelled"/> or an
    /// activation the engine would reject.
    /// </summary>
    private bool TryChooseTapAbility(DuelGame game, out int creatureIndex, out IReadOnlyList<SpellTarget>? targets)
    {
        creatureIndex = -1;
        targets = null;
        var foe = game.Opponent;

        // Untargeted abilities first: cheap, always positive-value tempo.
        for (var i = 0; i < Self.BattleZone.Count; i++)
        {
            var creature = Self.BattleZone[i];
            if (!game.CanUseTapAbility(Self, i))
                continue;
            foreach (var eff in creature.Card.TapAbilities.Where(e => e.Target == EffectTargetScope.None))
            {
                switch (eff.Id)
                {
                    case EffectId.Tap_Draw:
                    case EffectId.Tap_ChargeMana:
                        creatureIndex = i;
                        return true;
                    case EffectId.Tap_DiscardRandom when foe.Hand.Count >= 2:
                        creatureIndex = i;
                        return true;
                    case EffectId.Tap_GrantUnblockableCivEot
                        when Self.BattleZone.Any(c => CivOf(c.Card, eff.Data)):
                        creatureIndex = i;
                        return true;
                    case EffectId.Tap_GrantCanAttackUntappedCivEot
                        when Self.BattleZone.Any(c => CivOf(c.Card, eff.Data))
                             && foe.BattleZone.Any(c => !c.IsTapped):
                        creatureIndex = i;
                        return true;
                }
            }
        }

        // Targeted abilities: only when a genuinely useful legal target exists.
        for (var i = 0; i < Self.BattleZone.Count; i++)
        {
            var creature = Self.BattleZone[i];
            if (!game.CanUseTapAbility(Self, i))
                continue;
            foreach (var eff in creature.Card.TapAbilities.Where(e => e.Target != EffectTargetScope.None))
            {
                if (TryChooseTapTarget(game, eff, out var owner, out var index))
                {
                    creatureIndex = i;
                    targets = new[] { new SpellTarget(owner, index) };
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Pick the best legal card for tap-ability <paramref name="eff"/> out of the
    /// engine's own target pool: removal aims at the foe's biggest creature within
    /// the effect's filters, breather / grant abilities at the strongest own
    /// creature. Returning a mana card always costs tempo, so it is declined.
    /// </summary>
    private bool TryChooseTapTarget(DuelGame game, CardEffect eff, out Player owner, out int index)
    {
        owner = Self;
        index = -1;
        var foe = game.Opponent;
        var bestValue = float.NegativeInfinity;
        Player? bestOwner = null;
        var bestIndex = -1;

        void Consider(Player player, System.Collections.Generic.List<CardInstance> zone, float baseValue, float tapPenalty, float powerScale)
        {
            foreach (var item in game.TapTargetPool(Self, eff))
            {
                if (!zone.Contains(item))
                    continue;
                var c = zone.IndexOf(item);
                var value = baseValue + item.Card.Power / powerScale + (item.IsTapped ? tapPenalty : 0.5f);
                if (value > bestValue)
                {
                    bestValue = value;
                    bestOwner = player;
                    bestIndex = c;
                }
            }
        }

        switch (eff.Id)
        {
            case EffectId.Tap_ReturnToHand:
                Consider(foe, foe.BattleZone, 1f, -0.2f, 1000f);
                if (bestIndex < 0)
                {
                    // Bouncing an own creature is a last resort (e.g. the only move).
                    bestValue = float.NegativeInfinity;
                    Consider(Self, Self.BattleZone, 0f, -0.5f, 4000f);
                }
                break;

            case EffectId.Tap_DestroyPowerAtMost:
            case EffectId.Tap_DestroyBlocker:
                Consider(foe, foe.BattleZone, 1f, 0f, 1000f);
                break;

            case EffectId.Tap_TapOpponentCreature:
                foreach (var item in game.TapTargetPool(Self, eff))
                {
                    if (!foe.BattleZone.Contains(item) || item.IsTapped)
                        continue;
                    var c = foe.BattleZone.IndexOf(item);
                    var value = 0.5f + item.Card.Power / 2000f;
                    if (value > bestValue)
                    {
                        bestValue = value;
                        bestOwner = foe;
                        bestIndex = c;
                    }
                }
                break;

            case EffectId.Tap_BoostPowerEot:
            case EffectId.Tap_GrantUnblockableEot:
            case EffectId.Tap_GrantSlayerEot:
            case EffectId.Tap_GrantSpeedAttackerEot:
            case EffectId.Tap_GrantDoubleBreakerEot:
                Consider(Self, Self.BattleZone, 0.2f, -0.5f, 3000f);
                break;

            case EffectId.Tap_ReturnGraveCreatureToHand:
                Consider(Self, Self.Graveyard, 0.2f, 0f, 2000f);
                break;

            // Returning a mana card costs a land; only worth it in specific
            // decks - keep it conservative and never self-un-mana.
            case EffectId.Tap_ReturnCreatureFromManaToHand:
            case EffectId.Tap_ReturnManaCardToHand:
            case EffectId.Tap_ReturnSpellFromManaToHand:
            default:
                return false;
        }

        if (bestIndex < 0)
            return false;
        owner = bestOwner!;
        index = bestIndex;
        return true;
    }

    private static bool CivOf(Card card, string data) =>
        string.Equals(card.Civilization.ToString(), data, System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Pick one attack. Prefers killing tapped creatures it can overpower for free,
    /// then swings for shields (honoring "can't attack players / creatures",
    /// "can't be attacked" and outnumbered restrictions). Returns <c>true</c> and
    /// applies the attack (creature or direct) unless a Blocker could legally
    /// intercept a direct swing, in which case <paramref name="needsBlockChoice"/>
    /// is set and nothing is applied.
    /// </summary>
    private bool TryChooseAttack(DuelGame game, out int attackerIndex, out bool needsBlockChoice)
    {
        attackerIndex = -1;
        needsBlockChoice = false;

        var foe = game.Opponent;
        var outnumbered = foe.BattleZone.Count > Self.BattleZone.Count;

        var ready = Self.BattleZone
            .Select((c, i) => (Instance: c, Index: i))
            .Where(x => !x.Instance.IsTapped
                && !x.Instance.IsSummoningSick
                && x.Instance.Card.IsCreature
                && !(outnumbered && x.Instance.Card.HasKeyword(Keyword.CannotAttackOutnumbered)))
            .OrderByDescending(x => x.Instance.Card.Power)
            .ToList();
        if (ready.Count == 0)
            return false;

        // Free favourable kills first: out-power a tapped foe creature, nobody dies.
        for (var r = 0; r < ready.Count; r++)
        {
            var (attacker, aIdx) = ready[r];
            if (attacker.Card.HasKeyword(Keyword.CannotAttackCreatures))
                continue;
            var target = foe.BattleZone
                .Select((c, i) => (Instance: c, Index: i))
                .Where(x => x.Instance.Card.IsCreature
                    && !x.Instance.Card.HasKeyword(Keyword.CannotBeAttacked)
                    && (x.Instance.IsTapped || attacker.Card.HasKeyword(Keyword.CanAttackUntappedCreatures))
                    && attacker.Card.Power > x.Instance.Card.Power)
                .OrderByDescending(x => x.Instance.Card.Power)
                .FirstOrDefault();
            if (target.Instance is not null)
            {
                game.AttackCreature(aIdx, target.Index);
                attackerIndex = aIdx;
                return true;
            }
        }

        // Otherwise crash the shields. Creatures that "attack each turn" must swing
        // even for passive profiles; the rest only with enough aggression.
        var directCandidates = ready
            .Where(x => !x.Instance.Card.HasKeyword(Keyword.CannotAttackPlayers))
            .ToList();
        if (directCandidates.Count == 0)
            return false;
        var mustAttack = directCandidates.FirstOrDefault(x => x.Instance.Card.HasKeyword(Keyword.AttacksEachTurn));
        var swing = mustAttack.Instance is not null
            ? mustAttack
            : directCandidates.OrderByDescending(x => x.Instance.Card.Power).First();
        if (mustAttack.Instance is null && Profile.Aggression < 0.15f)
            return false;

        var canBlock = DuelGame.CanBeBlocked(swing.Instance.Card) && foe.BattleZone.Any(c =>
            c.Card.IsCreature && !c.IsTapped && c.Card.HasKeyword(Keyword.Blocker));
        if (canBlock)
        {
            attackerIndex = swing.Index;
            needsBlockChoice = true;
            return true;
        }
        game.AttackPlayer(swing.Index);
        attackerIndex = swing.Index;
        return true;
    }
}