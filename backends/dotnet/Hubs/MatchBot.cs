using System;
using System.Linq;
using DuelMasters.Domain;
using DuelMasters.Domain.Ai;

namespace DuelMasters.Server.Hubs;

/// <summary>
/// Pilots one side of an authoritative <see cref="MatchRoom"/> with the shared
/// <see cref="AiController"/>. It drives the bot's own turns exactly like the
/// interactive clients drive a human's: one decision at a time through the same
/// <see cref="MatchRoom"/> doors (so attacks pause for the human's blocker
/// window, shield-trigger windows owned by the human interrupt the bot's turn,
/// and so on). The hub calls <see cref="Drive"/> whenever a broadcast may have
/// put the ball back in the bot's court, and it stops as soon as the next
/// decision belongs to the human.
/// </summary>
public sealed class MatchBot
{
    /// <summary>Sentinel SignalR connection id used for a server-side bot seat.</summary>
    public const string ConnectionPrefix = "bot:";

    private readonly MatchRoom _room;

    public MatchBot(MatchRoom room, Player self, string side)
    {
        _room = room;
        Side = side;
        Self = self ?? throw new ArgumentNullException(nameof(self));
        Ai = new AiController(self);
    }

    /// <summary>The DuelSide string ("Player1"/"Player2") this bot occupies.</summary>
    public string Side { get; }

    /// <summary>The player the bot controls, for window-ownership checks.</summary>
    public Player Self { get; }

    /// <summary>The turn-taking brain.</summary>
    public AiController Ai { get; }

    /// <summary>
    /// Run every decision currently owed to the bot (its whole turn, its blocking
    /// choices, its own shield-trigger resolutions - or just one step until the
    /// game pauses on a human decision). All mutations happen under the match gate.
    /// Returns the number of broadcast-worthy changes applied (0 = nothing owed).
    /// </summary>
    public int Drive()
    {
        var changes = 0;
        _room.Execute(game =>
        {
            try
            {
                for (var guard = 0; guard < 600; guard++)
                {
                    if (!HasSomethingToDo(game))
                        return;

                    changes++;

                    if (game.ShieldTriggerWindowActive)
                    {
                        // Guard guarantees the owner is the bot; resolve without
                        // ever yielding an interactive window to the attacker side.
                        Ai.ResolveShieldTriggers(game);
                        continue;
                    }

                    if (game.IsScryWindowActive)
                    {
                        // The AI declines its owner's scry abilities, so a window
                        // here just resolves deterministically in draw order, like
                        // the zero-UI PlayTurn driver does.
                        game.SubmitScryOrder(game.ScryCards.ToList());
                        continue;
                    }

                    if (_room.HasPendingBlock)
                    {
                        // The HUMAN attacked the bot and the bot is the defender:
                        // decide the block the same way the interactive defender does.
                        var attackerIndex = _room.PendingAttackerIndex ?? -1;
                        if (Ai.DecideBlock(game, attackerIndex, out var blockerIndex))
                            _room.BlockPendingAttack(blockerIndex);
                        else
                            _room.PassPendingAttack();
                        continue;
                    }

                    // Advance the bot's own turn through the same phase ladder the
                    // interactive clients press (Untap -> Draw -> Main -> End-> next
                    // player's Untap) before/while making Main-phase decisions.
                    if (game.Phase == GamePhase.Untap) { game.StartTurn(); continue; }
                    if (game.Phase == GamePhase.Draw) { game.Draw(); continue; }
                    if (game.Phase != GamePhase.Main)
                    {
                        // End phase: close the turn; the next iteration moves to the
                        // human's Untap, where HasSomethingToDo turns false.
                        if (game.Phase == GamePhase.End) { game.EndTurn(); continue; }
                        return;
                    }

                    // It is the bot's Main phase: exactly one autonomous action.
                    var step = Ai.Step(game);
                    switch (step.Kind)
                    {
                        case AiStepKind.TurnEnded:
                            game.EndMainPhase();
                            continue;

                        case AiStepKind.NeedsBlockChoice:
                        {
                            // The bot swings into the human's Blocker. Route through
                            // the same declare step as a human attacker so the match
                            // pauses for the human's blocker window whenever one exists.
                            var (ok, blocks, _) = _room.DeclarePlayerAttack(step.AttackerIndex);
                            if (!ok)
                                return;
                            if (blocks > 0)
                                return; // the human decides now; Drive resumes after
                            continue;   // no blockers: the attack resolved itself
                        }

                        case AiStepKind.WaitingOnShieldTriggers:
                            return; // the human (defender) owns a trigger window

                        default:
                            continue; // ActionTaken
                    }
                }
            }
            catch (Exception)
            {
                // A surprising engine/AI state must not kill the hub request. Stop
                // driving; the match simply waits on the next human action, and the
                // E2E scenario surfaces real bot-pilot errors as failures.
            }
        }, out _);
        return changes;
    }

    /// <summary>True when this bot is the side the rules engine is next waiting on.</summary>
    private bool HasSomethingToDo(DuelGame game)
    {
        if (game.IsGameOver)
            return false;

        if (game.ShieldTriggerWindowActive)
            return ReferenceEquals(game.ShieldTriggerOwner, Self);

        if (game.IsScryWindowActive)
            return ReferenceEquals(game.ActivePlayer, Self);

        if (_room.HasPendingBlock)
            return _room.BlockDecisionSide() == Side;

        // The bot is expected whenever the engine is inside its own turn ladder, be
        // it Untap, Draw, Main or the End-phase close of that turn.
        return ReferenceEquals(game.ActivePlayer, Self) &&
            game.Phase is GamePhase.Untap or GamePhase.Draw or GamePhase.Main or GamePhase.End;
    }
}