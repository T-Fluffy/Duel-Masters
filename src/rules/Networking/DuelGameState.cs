using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using DuelMasters.Domain;

namespace DuelMasters.Domain.Networking;

/// <summary>
/// The authoritative, viewer-relative snapshot of a networked match. A <c>DuelGame</c>
/// runs entirely on the server; every client receives this state for its own side,
/// with the opponent's hidden information already redacted server-side.
/// </summary>
public sealed class DuelGameState
{
    [JsonPropertyName("matchCode")]
    public string MatchCode { get; set; } = "";

    /// <summary>The viewer's assigned side (used to decide which hand to reveal).</summary>
    [JsonPropertyName("yourSide")]
    public string YourSide { get; set; } = DuelSide.Player1;

    [JsonPropertyName("activeSide")]
    public string ActiveSide { get; set; } = DuelSide.Player1;

    [JsonPropertyName("phase")]
    public string Phase { get; set; } = "";

    [JsonPropertyName("turnNumber")]
    public int TurnNumber { get; set; }

    [JsonPropertyName("yourTurn")]
    public bool YourTurn { get; set; }

    [JsonPropertyName("canPlayMana")]
    public bool CanPlayMana { get; set; }

    [JsonPropertyName("canSummonOrCast")]
    public bool CanSummonOrCast { get; set; }

    [JsonPropertyName("canAttack")]
    public bool CanAttack { get; set; }

    [JsonPropertyName("shieldTriggerWindowActive")]
    public bool ShieldTriggerWindowActive { get; set; }

    /// <summary>Side that owns the pending Shield Trigger cards ("Player1"/"Player2"), when a window is open.</summary>
    [JsonPropertyName("shieldTriggerOwnerSide")]
    public string? ShieldTriggerOwnerSide { get; set; }

    /// <summary>True while a "put the looked-at deck cards back in order" decision is pending.</summary>
    [JsonPropertyName("scryWindowActive")]
    public bool ScryWindowActive { get; set; }

    /// <summary>Side whose deck is being ordered ("Player1"/"Player2"), while a scry window is open.</summary>
    [JsonPropertyName("scryOwnerSide")]
    public string? ScryOwnerSide { get; set; }

    /// <summary>The top-of-deck cards exposed by the pending scry window, in draw
    /// order (the engine's order, not the player's in-progress reordering). Only
    /// visible to the scry owner.</summary>
    [JsonPropertyName("scryCards")]
    public List<CardState> ScryCards { get; set; } = new();

    /// <summary>Hand indices (into the trigger owner's hand) of the pending Shield Trigger cards. Only visible to that owner.</summary>
    [JsonPropertyName("pendingTriggerHandIndices")]
    public List<int> PendingTriggerHandIndices { get; set; } = new();

    /// <summary>True while an attacking creature's "you may ..." trigger awaits an answer.</summary>
    [JsonPropertyName("attackDecisionWindowActive")]
    public bool AttackDecisionWindowActive { get; set; }

    /// <summary>Side that owns the pending "may" choice ("Player1"/"Player2"), while a window is open.</summary>
    [JsonPropertyName("attackDecisionOwnerSide")]
    public string? AttackDecisionOwnerSide { get; set; }

    /// <summary>The pending "may" choice kind: "lookAtShields", "searchToHand", "destroyCreature", "destroyPowerAtMost".</summary>
    [JsonPropertyName("attackDecisionKind")]
    public string? AttackDecisionKind { get; set; }

    /// <summary>Number of shields a pending shield-look may name (0 otherwise).</summary>
    [JsonPropertyName("attackDecisionShieldCount")]
    public int AttackDecisionShieldCount { get; set; }

    /// <summary>The power cap of a pending "destroy power N or less" choice (0 otherwise).</summary>
    [JsonPropertyName("attackDecisionValue")]
    public int AttackDecisionValue { get; set; }

    /// <summary>Legal creature targets for a pending "destroy a creature" attack-decision (both battle zones).</summary>
    [JsonPropertyName("attackDecisionTargets")]
    public List<TapTargetState> AttackDecisionTargets { get; set; } = new();

    /// <summary>True while an attack is declared and the defender may choose to block or pass.</summary>
    [JsonPropertyName("attackPending")]
    public bool AttackPending { get; set; }

    /// <summary>The battling attacker's battle-zone index, while <see cref="AttackPending"/> is true.</summary>
    [JsonPropertyName("attackPendingAttackerIndex")]
    public int AttackPendingAttackerIndex { get; set; }

    /// <summary>Number of ready Blocker creatures the defender may choose, while <see cref="AttackPending"/> is true.</summary>
    [JsonPropertyName("blocksAvailable")]
    public int BlocksAvailable { get; set; }

    [JsonPropertyName("isGameOver")]
    public bool IsGameOver { get; set; }

    [JsonPropertyName("winnerId")]
    public string? WinnerId { get; set; }

    [JsonPropertyName("players")]
    public List<PlayerState> Players { get; set; } = new();

    /// <summary>Build the viewer's state from an authoritative engine + which side they are.</summary>
    public static DuelGameState From(DuelGame game, string matchCode, string viewerSide)
    {
        var activeSide = DuelSide.FromIndex(game.ActivePlayer == game.Player1 ? 0 : 1);
        var phase = game.Phase.ToString();
        var yourTurn = activeSide == viewerSide;
        var canPlayMana = yourTurn && game.Phase == GamePhase.Main && !game.ManaChargedThisTurn && !game.IsGameOver;
        var canSummonOrCast = yourTurn && game.Phase == GamePhase.Main && !game.HasAttackedThisTurn && !game.IsGameOver;
        var canAttack = yourTurn && game.Phase == GamePhase.Main && !game.HasAttackedThisTurn && !game.IsGameOver;

        var p1 = DuelSide.FromIndex(0);
        var p2 = DuelSide.FromIndex(1);

        var triggerOwner = game.ShieldTriggerOwner;
        var triggerSide = triggerOwner is null ? null : DuelSide.FromIndex(triggerOwner == game.Player1 ? 0 : 1);
        var pendingIndices = new List<int>();
        if (triggerOwner is not null && triggerSide == viewerSide)
        {
            foreach (var pending in game.PendingShieldTriggers)
            {
                var handIndex = triggerOwner.Hand.IndexOf(pending);
                if (handIndex >= 0)
                    pendingIndices.Add(handIndex);
            }
        }

        var scryOwner = game.ScryOwner;
        var scrySide = scryOwner is null ? null : DuelSide.FromIndex(scryOwner == game.Player1 ? 0 : 1);
        var scryCards = new List<CardState>();
        if (scryOwner is not null && scrySide == viewerSide)
        {
            for (var i = 0; i < game.ScryCards.Count; i++)
                scryCards.Add(CardState.FromCard(game.ScryCards[i], $"Scry:{i}"));
        }

        var attackDecisionActive = game.AttackDecisionWindowActive;
        var attackOwnerSide = attackDecisionActive
            ? DuelSide.FromIndex(game.AttackDecisionSource?.Owner == game.Player1 ? 0 : 1)
            : null;
        var attackKind = attackDecisionActive ? game.PendingAttackDecision.ToString() : null;

        var state = new DuelGameState
        {
            MatchCode = matchCode,
            YourSide = viewerSide,
            ActiveSide = activeSide,
            Phase = phase,
            TurnNumber = game.TurnNumber,
            YourTurn = yourTurn,
            CanPlayMana = canPlayMana,
            CanSummonOrCast = canSummonOrCast,
            CanAttack = canAttack,
            ShieldTriggerWindowActive = game.ShieldTriggerWindowActive,
            ShieldTriggerOwnerSide = triggerSide,
            PendingTriggerHandIndices = pendingIndices,
            ScryWindowActive = game.IsScryWindowActive,
            ScryOwnerSide = scrySide,
            ScryCards = scryCards,
            IsGameOver = game.IsGameOver,
            WinnerId = game.Winner is null ? null : DuelSide.FromIndex(game.Winner == game.Player1 ? 0 : 1),
            AttackDecisionWindowActive = attackDecisionActive,
            AttackDecisionOwnerSide = attackOwnerSide,
            AttackDecisionKind = attackKind,
            AttackDecisionShieldCount = attackDecisionActive ? game.AttackDecisionShieldCount : 0,
            AttackDecisionValue = attackDecisionActive ? game.AttackDecisionValue : 0,
            Players =
            {
                PlayerState.From(game.Player1, p1, viewerSide),
                PlayerState.From(game.Player2, p2, viewerSide),
            },
        };

        // Annotate the active player's own battle-zone creatures with tap-ability
        // data: which can be used right now, and the legal targets each targeted
        // ability may choose from (server-computed from the engine's pool).
        var viewerState = state.Players.FirstOrDefault(p => p.Side == viewerSide);
        if (viewerState is not null && yourTurn && game.Phase == GamePhase.Main
            && !game.HasAttackedThisTurn && !game.ShieldTriggerWindowActive && !game.IsGameOver)
        {
            var active = game.ActivePlayer;
            for (var i = 0; i < active.BattleZone.Count && i < viewerState.BattleZone.Count; i++)
            {
                var creature = active.BattleZone[i];
                if (!creature.Card.HasTapAbility)
                    continue;

                var cardState = viewerState.BattleZone[i];
                cardState.HasTapAbility = true;
                if (!game.CanUseTapAbility(active, i))
                    continue;
                cardState.CanUseTapAbility = true;

                var targeted = creature.Card.TapAbilities.FirstOrDefault(e => e.Target != EffectTargetScope.None);
                if (targeted is not null)
                {
                    var targets = new List<TapTargetState>();
                    foreach (var item in game.TapTargetPool(active, targeted))
                    {
                        if (Locate(item, game, out var owner, out var index))
                        {
                            var side = ReferenceEquals(owner, game.Player1) ? p1 : p2;
                            var own = ReferenceEquals(owner, active);
                            targets.Add(new TapTargetState(side, index, TapTargetLabel(own, item.Card)));
                        }
                    }
                    if (targets.Count > 0)
                        cardState.TapAbilityTargets = targets;
                }

                var races = creature.Card.TapAbilities.FirstOrDefault(e => e.Target == EffectTargetScope.None
                    && e.Id is EffectId.Tap_ChooseRaceUntapEot or EffectId.Tap_ChooseRaceGrantSlayerEot
                        or EffectId.Tap_ChooseRaceToHandEot or EffectId.Tap_ChooseRaceMustAttackPowerAttackerEot
                        or EffectId.Tap_ChooseRaceUnblockableByPowerEot);
                if (races is not null)
                    cardState.TapAbilityRaces = game.LegalRaceChoices(active).ToList();

                var decision = creature.Card.TapAbilities.FirstOrDefault(e => e.Target == EffectTargetScope.None
                    && e.Id is EffectId.Tap_ChooseShieldLook or EffectId.Tap_ScryTopCards);
                if (decision is not null)
                    cardState.TapDecisionKind = decision.Id == EffectId.Tap_ChooseShieldLook ? "shield" : "scry";
            }

            // Crew annotations: for each crew creature, list the own creatures that can
            // pay the crew tap right now (the holder itself plus matching-civilization
            // ready creators). Decision-based abilities cannot resolve through the crew
            // path, so those creatures are excluded from the payer list.
            for (var i = 0; i < active.BattleZone.Count && i < viewerState.BattleZone.Count; i++)
            {
                var holder = active.BattleZone[i];
                var holderState = viewerState.BattleZone[i];
                if (!holder.Card.HasCrew)
                    continue;
                if (holder.Card.TapAbilities.Any(e => e.Id is EffectId.Tap_ChooseShieldLook or EffectId.Tap_ScryTopCards))
                    continue;
                var payers = new List<int>();
                for (var payerIndex = 0; payerIndex < active.BattleZone.Count; payerIndex++)
                    if (game.CanUseCrewAbility(active, i, payerIndex))
                        payers.Add(payerIndex);
                if (payers.Count == 0)
                    continue;
                holderState.HasCrew = true;
                holderState.CrewPayerIndices = payers;
            }
        }

        // Annotate the source creature of an active attack-decision window so the
        // viewer's arena can render the prompt at the attacking creature's card.
        if (attackDecisionActive && attackOwnerSide == viewerSide && viewerState is not null)
        {
            var source = game.AttackDecisionSource;
            if (source?.Owner is not null)
            {
                var sourceIndex = source.Owner.BattleZone.IndexOf(source);
                var sourceBattle = sourceIndex >= 0 && sourceIndex < viewerState.BattleZone.Count
                    ? viewerState.BattleZone[sourceIndex]
                    : null;
                if (sourceBattle is not null)
                {
                    sourceBattle.AttackDecisionPending = true;
                    sourceBattle.AttackDecisionKind = attackKind;
                    sourceBattle.AttackDecisionShieldCount = game.AttackDecisionShieldCount;
                    sourceBattle.AttackDecisionValue = game.AttackDecisionValue;
                }
            }

            // For destroy-kind decisions, serialize the legal target pool.
            if (game.PendingAttackDecision is DuelGame.AttackDecisionKind.DestroyCreature
                or DuelGame.AttackDecisionKind.DestroyPowerAtMost)
            {
                foreach (var target in game.AttackDecisionTargets)
                {
                    if (Locate(target, game, out var owner, out var index))
                    {
                        var side = ReferenceEquals(owner, game.Player1) ? p1 : p2;
                        var own = ReferenceEquals(owner, game.ActivePlayer);
                        state.AttackDecisionTargets.Add(new TapTargetState(side, index, TapTargetLabel(own, target.Card)));
                    }
                }
            }
        }

        return state;
    }

    /// <summary>Find which zone list of which player a tap-ability pool card lives in.</summary>
    private static bool Locate(CardInstance instance, DuelGame game, out Player owner, out int index)
    {
        var active = game.ActivePlayer;
        var opponent = game.Opponent;
        if (active.BattleZone.Contains(instance))
        {
            owner = active;
            index = active.BattleZone.IndexOf(instance);
            return true;
        }
        if (opponent.BattleZone.Contains(instance))
        {
            owner = opponent;
            index = opponent.BattleZone.IndexOf(instance);
            return true;
        }
        if (active.ManaZone.Contains(instance))
        {
            owner = active;
            index = active.ManaZone.IndexOf(instance);
            return true;
        }
        if (opponent.ManaZone.Contains(instance))
        {
            owner = opponent;
            index = opponent.ManaZone.IndexOf(instance);
            return true;
        }
        if (active.Graveyard.Contains(instance))
        {
            owner = active;
            index = active.Graveyard.IndexOf(instance);
            return true;
        }
        owner = active;
        index = -1;
        return false;
    }

    private static string TapTargetLabel(bool own, Card card)
    {
        var ownerWord = own ? "Your" : "Their";
        return card.IsCreature
            ? $"{ownerWord} creature - {card.Name} ({card.Power} power)"
            : $"{ownerWord} card - {card.Name}";
    }
}
