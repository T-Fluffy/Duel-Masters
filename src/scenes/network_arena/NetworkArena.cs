using System;
using System.Collections.Generic;
using System.Linq;
using DuelMasters.Core;
using DuelMasters.Domain;
using DuelMasters.Domain.Networking;
using DuelMasters.Gameplay.Audio;
using DuelMasters.Gameplay.CardView;
using DuelMasters.Gameplay.Fx;
using DuelMasters.Networking;
using DuelMasters.Resources;
using DuelMasters.UI;
using DuelMasters.UI.Settings;
using Godot;

namespace DuelMasters.Scenes.NetworkArena;

/// <summary>
/// Phase 4: rendered view of the server-authoritative duel. The rules engine runs
/// only on the backend; this scene draws the viewer-relative <see cref="DuelGameState"/>
/// pushed by the hub and forwards player intents (mana, summon, cast, evolve, attacks,
/// blocking, shield triggers, turn flow) via <see cref="NetworkClient"/>. No local
/// <c>DuelGame</c> exists here - legality hints are recomputed from the card catalog.
/// </summary>
public partial class NetworkArena : Control
{
	private enum Mode
	{
		Idle,
		SelectAttacker,
		SelectBlock,
		SelectSpellTarget,
		SelectShieldTarget,
		SelectSummonTarget,
		SelectEvolveTarget,
		EvolveBase,
		SelectTapTarget,
		SelectShieldLook,
	}

	private DuelGameState _state = null!;
	private readonly Dictionary<string, string> _artByCardId = new();
	private readonly Dictionary<string, Card> _cardsByCardId = new();

	// When a rule action taps several mana cards at once (summon / spell costs and
	// the start-of-turn untap), animate each card's tap pose one-by-one with this
	// pause between cards so the player can follow the mana being spent. Tunable in
	// the NetworkArena scene inspector.
	[Export] private float ManaTapStaggerSeconds = 2.0f;

	// Tapped flags of each mana slot from the previous broadcast, so tap changes are
	// animated instead of snapping (mana is append-only, so slots stay stable).
	private readonly List<bool> _prevOppManaTapped = new();
	private readonly List<bool> _prevMyManaTapped = new();
	private Mode _mode = Mode.Idle;
	private int _attackerIndex = -1;
	private int _spellHandIndex = -1;
	private int _triggerHandIndex = -1;
	private int _summonHandIndex = -1;
	private int _evolveHandIndex = -1;
	private int _tapCreatureIndex = -1;
	private string? _pendingEvolveTargetSide;
	private int _pendingEvolveTargetIndex = -1;

	private VBoxContainer _oppHand = null!;
	private VBoxContainer _oppShields = null!;
	private VBoxContainer _oppMana = null!;
	private VBoxContainer _oppBattle = null!;
	private VBoxContainer _myShields = null!;
	private VBoxContainer _myBattle = null!;
	private VBoxContainer _myMana = null!;
	private VBoxContainer _myHand = null!;
	private Button _oppGrave = null!;
	private Button _myGrave = null!;

	private Label _status = null!;
	private Label _prompt = null!;
	private HBoxContainer _actionBar = null!;
	private Button _endTurn = null!;
	private Button _rematch = null!;

	private Control _overlay = null!;
	private ColorRect _overlayDim = null!;
	private CenterContainer _overlayCenter = null!;
	private PanelContainer _overlayPanel = null!;
	private VBoxContainer _overlayBox = null!;
	private enum OverlayKind { None, Look, Trigger, Decision }
	private OverlayKind _overlayKind = OverlayKind.None;

	// Scry window ("look at the top N cards, then put them back in any order").
	// The reorderable tray is rendered from the owner-visible ScryCards snapshot and
	// resolved with the server by sending back the cards' instance ids in the new order.
	private PanelContainer _scryPopup = null!;
	private VBoxContainer _scryPopupBox = null!;
	private readonly List<CardState> _scryCards = new();
	private string _scryFingerprint = "";
	private bool _scrySendPending;

	// Phase 5: VFX + SFX overlay and the shield-count deltas that trigger shatters.
	private FxManager _fxManager = null!;
	private bool _winnerShown;
	private int _prevOppShieldCount = -1;
	private int _prevMyShieldCount = -1;

	// Attack-decision overlay ("you may ..." attack triggers): picked shields for a
	// shield-look choice and the confirm button that submits them.
	private readonly List<int> _decisionShieldPicks = new();
	private Button? _decisionShieldConfirm;

	public override void _Ready()
	{
		foreach (var r in CardCatalog.Load())
		{
			_artByCardId[r.Card.Id] = r.ImagePath;
			_cardsByCardId[r.Card.Id] = r.Card;
		}
		BuildLayout();
		_fxManager = new FxManager();
		AddChild(_fxManager);
		if (NetworkClient.CurrentState is { } st)
			_state = st;
		GameSettings.CardSizeMultiplierChanged += Refresh;
		Refresh();
	}

	public override void _ExitTree()
	{
		GameSettings.CardSizeMultiplierChanged -= Refresh;
	}

	private (float W, float H) ScaledCardSize()
	{
		var m = GameSettings.CardSizeMultiplier;
		return (140f * m, 195f * m);
	}

	public override void _Process(double delta)
	{
		if (NetworkClient.TryDequeueError(out var err))
			Notice(err);
		if (NetworkClient.TryDequeueWinner(out var winner))
		{
			_endTurn.Disabled = true;
			Notice($"Winner: {winner}");
		}
		if (NetworkClient.TryDequeueState(out var state))
		{
			// A rematch restart arrives as a fresh turn-1 game: clear the previous
			// winner's fanfare and shield-tracking so the new game starts clean.
			if (_state is { IsGameOver: true } && !state.IsGameOver && state.TurnNumber == 1)
			{
				_winnerShown = false;
				_prevOppShieldCount = -1;
				_prevMyShieldCount = -1;
				ResetInteraction();
				HideOverlay();
			}
			_state = state;
			Refresh();
		}
		if (NetworkClient.TryDequeuePeeked(out var peeked))
			ShowLookAt(peeked);

		// Auto-advance the Untap -> Draw -> Main steps at the start of my turn.
		if (_state is not null && _state.YourTurn && !_state.IsGameOver)
		{
			if (IsPhase("Untap"))
				NetworkClient.StartTurn();
			else if (IsPhase("Draw"))
				NetworkClient.Draw();
		}
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape }
			or InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
		{
			// The defender must actively choose a blocker or press Pass - Esc would
			// strand the pending attack with no way back to that choice.
			if (_state is { AttackPending: true } && !_state.YourTurn)
				return;
			// Choosing a Shield Trigger target: cancelling leaves the triggers in hand.
			if (_mode == Mode.SelectShieldTarget)
			{
				NetworkClient.DeclineShieldTriggers();
				ResetInteraction();
				return;
			}
			// Shield-look is optional: cancelling simply skips the peek.
			if (_mode == Mode.SelectShieldLook)
			{
				ResetInteraction();
				return;
			}
			// Cancelling an open scry window returns the deck to its drawn order.
			if (ScryWindowIsMine)
			{
				SubmitIdentityScryOrder();
				return;
			}
			// Just close an open card-inspector; keep the current selection/mode
			// (e.g. the attacker menu stays usable after looking at the monster).
			if (_overlayKind == OverlayKind.Look)
			{
				HideOverlay();
				return;
			}
			CancelInteraction();
		}
	}

	// --------------------------------------------------------------- layout

	private void BuildLayout()
	{
		var margin = new MarginContainer();
		margin.SetAnchorsPreset(LayoutPreset.FullRect);
		margin.AddThemeConstantOverride("margin_left", 24);
		margin.AddThemeConstantOverride("margin_top", 16);
		margin.AddThemeConstantOverride("margin_right", 24);
		margin.AddThemeConstantOverride("margin_bottom", 16);
		AddChild(margin);

		var root = new VBoxContainer();
		root.AddThemeConstantOverride("separation", 10);
		margin.AddChild(root);

		var oppRow = new HBoxContainer();
		oppRow.AddThemeConstantOverride("separation", 16);
		_oppHand = BuildZone("OPPONENT HAND", out _);
		_oppShields = BuildZone("SHIELDS", out _);
		_oppMana = BuildZone("OPPONENT MANA", out _);
		_oppBattle = BuildZone("OPPONENT BATTLE", out _);
		oppRow.AddChild(_oppHand);
		oppRow.AddChild(_oppShields);
		oppRow.AddChild(_oppMana);
		oppRow.AddChild(_oppBattle);
		root.AddChild(oppRow);

		_status = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
		_status.AddThemeFontSizeOverride("font_size", 22);
		root.AddChild(_status);

		var myRow = new HBoxContainer();
		myRow.AddThemeConstantOverride("separation", 16);
		_myShields = BuildZone("YOUR SHIELDS", out _);
		_myBattle = BuildZone("YOUR BATTLE", out _);
		_myMana = BuildZone("YOUR MANA", out _);
		_myHand = BuildZone("YOUR HAND", out _);
		myRow.AddChild(_myShields);
		myRow.AddChild(_myBattle);
		myRow.AddChild(_myMana);
		myRow.AddChild(_myHand);
		root.AddChild(myRow);

		var graveRow = new HBoxContainer();
		graveRow.AddThemeConstantOverride("separation", 12);
		_myGrave = new Button { Text = "My graveyard (0)" };
		_myGrave.Pressed += () => ShowGrave(Me());
		_oppGrave = new Button { Text = "Opponent's graveyard (0)" };
		_oppGrave.Pressed += () => ShowGrave(Opp());
		graveRow.AddChild(_myGrave);
		graveRow.AddChild(_oppGrave);
		graveRow.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
		root.AddChild(graveRow);

		_prompt = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
		_prompt.AddThemeFontSizeOverride("font_size", 16);
		root.AddChild(_prompt);

		var footer = new HBoxContainer();
		footer.AddThemeConstantOverride("separation", 16);
		_actionBar = new HBoxContainer();
		_actionBar.AddThemeConstantOverride("separation", 10);
		footer.AddChild(_actionBar);
		_endTurn = new Button { Text = "End Turn" };
		_endTurn.Pressed += OnEndTurn;
		footer.AddChild(_endTurn);
		var leave = new Button { Text = "Leave" };
		leave.Pressed += OnLeave;
		footer.AddChild(leave);
		_rematch = new Button { Text = "Rematch", Visible = false };
		_rematch.Pressed += OnRematch;
		footer.AddChild(_rematch);
		root.AddChild(footer);

		BuildOverlay();
		BuildScryPopup();
		AddChild(new SceneOptionsMenu { ShowBackToMenu = true });
	}

	private void BuildScryPopup()
	{
		var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
		center.SetAnchorsPreset(LayoutPreset.FullRect);
		AddChild(center);

		_scryPopup = new PanelContainer { Visible = false };
		_scryPopup.AddThemeStyleboxOverride("panel", UiStyles.ModalCard());
		center.AddChild(_scryPopup);

		_scryPopupBox = new VBoxContainer();
		_scryPopupBox.AddThemeConstantOverride("separation", 10);
		_scryPopupBox.CustomMinimumSize = new Vector2(620, 0);
		_scryPopup.AddChild(_scryPopupBox);
	}

	private VBoxContainer BuildZone(string caption, out Label title)
	{
		var box = new VBoxContainer();
		box.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		box.AddThemeConstantOverride("separation", 6);
		title = new Label { Text = caption };
		title.AddThemeFontSizeOverride("font_size", 13);
		box.AddChild(title);
		var flow = new HFlowContainer();
		flow.AddThemeConstantOverride("h_separation", 8);
		flow.AddThemeConstantOverride("v_separation", 8);
		box.AddChild(flow);
		return box;
	}

	private void BuildOverlay()
	{
		_overlay = new Control { Visible = false };
		_overlay.SetAnchorsPreset(LayoutPreset.FullRect);
		AddChild(_overlay);

		_overlayDim = new ColorRect { Color = new Color(0.01f, 0.02f, 0.04f, 0.88f) };
		_overlayDim.SetAnchorsPreset(LayoutPreset.FullRect);
		_overlay.AddChild(_overlayDim);

		var catcher = new Control { MouseFilter = MouseFilterEnum.Stop };
		catcher.SetAnchorsPreset(LayoutPreset.FullRect);
		catcher.GuiInput += @event =>
		{
			if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }
				&& _overlayKind == OverlayKind.Look)
				HideOverlay();
		};
		_overlay.AddChild(catcher);

		_overlayCenter = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
		_overlayCenter.SetAnchorsPreset(LayoutPreset.FullRect);
		_overlay.AddChild(_overlayCenter);

		_overlayPanel = new PanelContainer();
		_overlayPanel.AddThemeStyleboxOverride("panel", UiStyles.ModalCard());
		_overlayCenter.AddChild(_overlayPanel);

		_overlayBox = new VBoxContainer();
		_overlayBox.AddThemeConstantOverride("separation", 8);
		_overlayBox.CustomMinimumSize = new Vector2(260, 0);
		_overlayPanel.AddChild(_overlayBox);
	}

	private void HideOverlay()
	{
		_overlayKind = OverlayKind.None;
		_overlay.Visible = false;
	}

	private void SetOverlayBoxTitle(string text)
	{
		var title = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
		title.AddThemeFontSizeOverride("font_size", 16);
		title.AddThemeColorOverride("font_color", UiStyles.AccentText);
		title.HorizontalAlignment = HorizontalAlignment.Center;
		_overlayBox.AddChild(title);
	}

	private void SetOverlayBoxNote(string text)
	{
		var note = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
		note.AddThemeFontSizeOverride("font_size", 12);
		note.AddThemeColorOverride("font_color", UiStyles.MutedText);
		note.HorizontalAlignment = HorizontalAlignment.Center;
		_overlayBox.AddChild(note);
	}

	// --------------------------------------------------------------- actions

	private void OnEndTurn()
	{
		if (_state is null || !_state.YourTurn || _state.IsGameOver)
			return;
		if (IsPhase("Main"))
			NetworkClient.EndMainPhase();
		else if (IsPhase("End"))
			NetworkClient.EndTurn();
	}

	private async void OnLeave()
	{
		await NetworkClient.DisconnectAsync();
		GetTree().ChangeSceneToFile("res://src/scenes/network_lobby/NetworkLobby.tscn");
	}

	private async void OnRematch()
	{
		if (_state is { IsGameOver: false })
			return;
		_rematch.Disabled = true;
		if (await NetworkClient.RequestRematchAsync())
			return; // rested in place - the fresh state broadcast re-enables the button.
		_rematch.Disabled = false;
		Prompt("Rematch requested - waiting for the opponent...");
	}

	private void OnHandClicked(int index)
	{
		if (_state is null || _state.IsGameOver)
			return;

		// Shield-trigger window interrupts everything: clicking a hand card only inspects.
		if (TriggerWindowIsMine)
		{
			ShowLookAt(MyHandCard(index));
			return;
		}

		if (!_state.YourTurn || !IsPhase("Main"))
		{
			if (MyHandCard(index) is { } handCard)
				ShowLookAt(handCard);
			return;
		}

		_mode = Mode.Idle;
		BuildHandActions(index);
	}

	private void BuildHandActions(int handIndex)
	{
		ClearActions();
		var hand = Me().Hand;
		if (handIndex < 0 || handIndex >= hand.Count)
			return;
		var cardState = hand[handIndex];
		var card = CardFor(cardState);

		AddAction($"Look: {card?.Name ?? cardState.Name}", () => ShowLookAt(cardState));

		if (_state.CanPlayMana)
			AddAction($"Charge Mana: {card?.Name ?? cardState.Name}", () => NetworkClient.PlayMana(handIndex));

		if (!_state.CanSummonOrCast)
			return;

		if (card is null)
		{
			if (cardState.CardType == "Creature")
				AddAction("Summon", () => NetworkClient.SummonCreature(handIndex));
			else if (cardState.CardType == "Spell")
				AddAction("Cast spell", () => NetworkClient.CastSpell(handIndex));
			return;
		}

		if (card.IsEvolution)
		{
			if (HasMatchingBase(card))
				AddAction($"Evolve onto a {card.EvolutionOf} creature", () => SelectEvolveBase(handIndex));
			if (DuelGame.HasOnPlayTargetChoice(card) && AnyLegalOnPlayTarget(card))
				AddAction("Evolve & use ability", () => SelectEvolveTarget(handIndex));
			SetOverlayAdhocNote(cardState, $"Free - place it on top of one of your {card.EvolutionOf} creatures.");
		}
		else if (card.IsCreature)
		{
			AddAction($"Summon: {card.Name}", () => NetworkClient.SummonCreature(handIndex));
			if (DuelGame.HasOnPlayTargetChoice(card) && AnyLegalOnPlayTarget(card))
				AddAction("Summon & use ability", () => SelectSummonTarget(handIndex));
		}
		else if (card.CardType == CardType.Spell)
		{
			if (card.Effects.Any(e => e.NeedsTarget) && AnyLegalSpellTarget(card))
				AddAction($"Cast & choose target: {card.Name}", () => SelectSpellTarget(handIndex));
			else
				AddAction($"Cast: {card.Name}", () => NetworkClient.CastSpell(handIndex));
		}
	}

	private void SetOverlayAdhocNote(CardState _, string text)
	{
		// Keep a compact hint inside the action bar area via the prompt label.
		var note = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
		note.AddThemeFontSizeOverride("font_size", 11);
		note.AddThemeColorOverride("font_color", UiStyles.MutedText);
		note.CustomMinimumSize = new Vector2(180, 0);
		_actionBar.AddChild(note);
	}

	/// <summary>
	/// Action-bar menu shown right after picking a battle-zone creature as the
	/// attacker: inspect the monster or confirm and pick an enemy target. Clicking
	/// the selected attacker again re-opens it; Esc / another selection cancels.
	/// </summary>
	private void BuildAttackerActions(CardState attacker, int attackerIndex)
	{
		ClearActions();
		var name = attacker.Name;
		AddAction($"Look: {name}", () => ShowLookAt(attacker));
		AddAction("Attack", () =>
		{
			Prompt($"Pick a target for {name}: a tapped enemy creature, or the enemy shields. Esc to cancel.");
		});

		// A ready creature may pay a tap to resolve its Tap Ability. The server has
		// already pre-validated whether it is usable and lists its legal targets and
		// race choices, so the client only ever offers valid options.
		if (attacker.HasTapAbility && attacker.CanUseTapAbility)
		{
			AddAction("Use tap ability", () =>
			{
				switch (attacker.TapDecisionKind)
				{
					case "shield":
						SelectShieldLook(attackerIndex, attacker);
						return;
					case "scry":
						NetworkClient.ActivateTapAbilityScry(attackerIndex);
						ResetInteraction();
						return;
				}
				if (attacker.TapAbilityRaces is { Count: > 0 })
					SelectTapRace(attackerIndex, attacker);
				else if (attacker.TapAbilityTargets is { Count: > 0 })
					SelectTapTarget(attackerIndex, attacker);
				else
				{
					NetworkClient.ActivateTapAbility(attackerIndex);
					ResetInteraction();
				}
			});
		}

		// A crew creature may have its tap paid by any eligible own creature (the
		// server lists the payer indices in the battle-zone snapshot).
		if (attacker.HasCrew && attacker.CrewPayerIndices.Count > 0)
		{
			AddAction("Use crew ability", () => SelectCrewPayer(attackerIndex, attacker));
		}
	}

	private void SelectCrewPayer(int attackerIndex, CardState attacker)
	{
		_mode = Mode.SelectTapTarget;
		_tapCreatureIndex = attackerIndex;
		ClearActions();
		Prompt("Choose the creature that pays the crew tap, or press Esc to cancel.");
		var me = Me();
		foreach (var payerIndex in attacker.CrewPayerIndices)
		{
			if (payerIndex < 0 || payerIndex >= me.BattleZone.Count)
				continue;
			var payer = me.BattleZone[payerIndex];
			var caption = ReferenceEquals(payer, attacker)
				? $"Pay itself ({payer.Name})"
				: $"Pay with {payer.Name}";
			AddAction(caption, () =>
			{
				ResolveCrewWith(attackerIndex, attacker, payerIndex);
			});
		}
		AddAction("Cancel", () =>
		{
			ResetInteraction();
			Prompt("");
		});
	}

	private void ResolveCrewWith(int attackerIndex, CardState attacker, int payerIndex)
	{
		switch (attacker.TapDecisionKind)
		{
			case "shield":
			case "scry":
				Notice($"{attacker.Name}'s crew ability needs a decision that is not modelled yet.");
				return;
		}
		if (attacker.TapAbilityRaces is { Count: > 0 } races)
		{
			_mode = Mode.SelectTapTarget;
			_tapCreatureIndex = attackerIndex;
			ClearActions();
			Prompt($"Choose a race for {attacker.Name}'s crew ability, or press Esc to cancel.");
			foreach (var race in races)
				AddAction(race, () => NetworkClient.ActivateCrewAbilityRace(attackerIndex, payerIndex, race));
			AddAction("Cancel", () =>
			{
				ResetInteraction();
				Prompt("");
			});
			return;
		}
		if (attacker.TapAbilityTargets is { Count: > 0 } targets)
		{
			_mode = Mode.SelectTapTarget;
			_tapCreatureIndex = attackerIndex;
			ClearActions();
			Prompt($"Choose the target for {attacker.Name}'s crew ability, or press Esc to cancel.");
			foreach (var target in targets)
				AddAction(target.Label, () => NetworkClient.ActivateCrewAbilityTargeted(
					attackerIndex, payerIndex, target.Side, target.Index));
			AddAction("Cancel", () =>
			{
				ResetInteraction();
				Prompt("");
			});
			return;
		}
		NetworkClient.ActivateCrewAbility(attackerIndex, payerIndex);
		ResetInteraction();
	}

	private void SelectShieldLook(int creatureIndex, CardState attacker)
	{
		_mode = Mode.SelectShieldLook;
		_tapCreatureIndex = creatureIndex;
		ClearActions();
		Prompt($"Choose one of your shields for {attacker.Name}'s ability: just click a shield card, or press Esc to skip.");
		AddAction("Cancel", () =>
		{
			ResetInteraction();
			Prompt("");
		});
	}

	private void SelectTapRace(int creatureIndex, CardState attacker)
	{
		_mode = Mode.SelectTapTarget;
		_tapCreatureIndex = creatureIndex;
		ClearActions();
		Prompt($"Choose a race for {attacker.Name}'s tap ability, or press Esc to cancel.");
		foreach (var race in attacker.TapAbilityRaces ?? new List<string>())
		{
			AddAction(race, () => NetworkClient.ActivateTapAbilityRace(creatureIndex, race));
		}
		AddAction("Cancel", () =>
		{
			ResetInteraction();
			Prompt("");
		});
	}

	private void SelectTapTarget(int creatureIndex, CardState attacker)
	{
		_mode = Mode.SelectTapTarget;
		_tapCreatureIndex = creatureIndex;
		ClearActions();
		Prompt($"Choose the target for {attacker.Name}'s tap ability, or press Esc to cancel.");
		foreach (var target in attacker.TapAbilityTargets ?? new List<TapTargetState>())
		{
			AddAction(target.Label, () => NetworkClient.ActivateTapAbilityTargeted(creatureIndex, target.Side, target.Index));
		}
		AddAction("Cancel", () =>
		{
			ResetInteraction();
			Prompt("");
		});
	}

	private void SelectEvolveBase(int handIndex)
	{
		_mode = Mode.EvolveBase;
		_evolveHandIndex = handIndex;
		_pendingEvolveTargetSide = null;
		_pendingEvolveTargetIndex = -1;
		ClearActions();
		Prompt($"Evolve onto a matching-race creature: click one of your creatures, or press Esc to cancel.");
	}

	private void SelectEvolveTarget(int handIndex)
	{
		_mode = Mode.SelectEvolveTarget;
		_evolveHandIndex = handIndex;
		ClearActions();
		Prompt("Choose an on-play target for the evolution: click a legal creature, or press Esc to cancel.");
	}

	private void SelectSummonTarget(int handIndex)
	{
		_mode = Mode.SelectSummonTarget;
		_summonHandIndex = handIndex;
		ClearActions();
		Prompt("Choose an on-play target for the creature: click a legal creature, or press Esc to cancel.");
	}

	private void SelectSpellTarget(int handIndex)
	{
		_mode = Mode.SelectSpellTarget;
		_spellHandIndex = handIndex;
		ClearActions();
		Prompt("Choose a target for the spell: click a legal creature, or press Esc to cancel.");
	}

	private void OnBattleClicked(bool mine, int index)
	{
		if (_state is null || _state.IsGameOver)
			return;

		var zone = mine ? Me().BattleZone : Opp().BattleZone;
		if (index < 0 || index >= zone.Count)
			return;
		var target = zone[index];

		if (_state.AttackPending && !_state.YourTurn)
		{
			// I am the defender: reply to the pending attack.
			if (!mine)
			{
				ShowLookAt(target);
				return;
			}
			if (CanBlockWith(target))
			{
				ClearActions();
				NetworkClient.BlockAttack(index);
				ResetInteraction();
			}
			else
			{
				Notice(target.CountOnly ? "That creature cannot block." : $"{target.Name} cannot block (needs the Blocker keyword and to be untapped).");
			}
			return;
		}

		switch (_mode)
		{
			case Mode.Idle:
				if (mine && _state.YourTurn && _state.CanAttack && IsReadyAttacker(target))
				{
					_attackerIndex = index;
					_mode = Mode.SelectAttacker;
					BuildAttackerActions(target, index);
					Prompt("Attack selected: look at the monster or press Attack, then click a tapped enemy creature or the enemy shields.");
				}
				else
				{
					ShowLookAt(target);
				}
				break;

			case Mode.SelectAttacker:
				if (mine)
				{
					if (index == _attackerIndex)
					{
						// Re-clicking the selected attacker re-opens (or dismisses) its menu.
						if (_actionBar.GetChildCount() > 0)
							ClearActions();
						else
							BuildAttackerActions(target, index);
					}
					else if (_state.CanAttack && IsReadyAttacker(target))
					{
						_attackerIndex = index;
						BuildAttackerActions(target, index);
						Prompt($"Picked {target.Name} as the attacker: look at it or press Attack, then click a tapped enemy creature or the enemy shields.");
					}
					else
					{
						CancelInteraction();
					}
					break;
				}
				if (IsAttackableByMe(target))
				{
					NetworkClient.AttackCreature(_attackerIndex, index);
					ResetInteraction();
				}
				else
				{
					Notice("Under normal rules you may only attack a tapped creature.");
				}
				break;

			case Mode.SelectSpellTarget when _spellHandIndex >= 0:
				if (ForHandCard(_spellHandIndex) is { } spell
					&& IsCreature(target)
					&& IsLegalSpellTarget(spell, target, mine))
				{
					var hand = _spellHandIndex;
					NetworkClient.CastSpellTargeted(hand, SideFor(mine), index);
					ResetInteraction();
				}
				else
				{
					ShowLookAt(target);
				}
				break;

			case Mode.SelectShieldTarget when _triggerHandIndex >= 0:
				if (ForHandCard(_triggerHandIndex) is { } triggerCard
					&& IsCreature(target)
					&& IsLegalSpellTarget(triggerCard, target, mine))
				{
					var hand = _triggerHandIndex;
					NetworkClient.PlayShieldTriggerTargeted(hand, SideFor(mine), index);
					ResetInteraction();
				}
				else
				{
					ShowLookAt(target);
				}
				break;

			case Mode.SelectSummonTarget when _summonHandIndex >= 0:
				if (ForHandCard(_summonHandIndex) is { } summonCard
					&& IsCreature(target)
					&& IsLegalOnPlayTarget(summonCard, target, mine))
				{
					var hand = _summonHandIndex;
					NetworkClient.SummonCreatureTargeted(hand, SideFor(mine), index);
					ResetInteraction();
				}
				else
				{
					ShowLookAt(target);
				}
				break;

			case Mode.SelectEvolveTarget when _evolveHandIndex >= 0:
				if (ForHandCard(_evolveHandIndex) is { } evolveCard
					&& IsCreature(target)
					&& IsLegalOnPlayTarget(evolveCard, target, mine))
				{
					_pendingEvolveTargetSide = SideFor(mine);
					_pendingEvolveTargetIndex = index;
					_mode = Mode.EvolveBase;
					ClearActions();
					Prompt("Choose the evolution base: click one of your matching-race creatures, or press Esc to cancel.");
				}
				else
				{
					ShowLookAt(target);
				}
				break;

			case Mode.EvolveBase when _evolveHandIndex >= 0:
				if (mine && IsCreature(target) && CardFor(target) is { } baseCard
					&& ForHandCard(_evolveHandIndex) is { } evolution
					&& DuelGame.IsEvolutionBase(evolution, baseCard))
				{
					var hand = _evolveHandIndex;
					if (_pendingEvolveTargetSide is { } side && _pendingEvolveTargetIndex >= 0)
						NetworkClient.EvolveCreatureTargeted(hand, index, side, _pendingEvolveTargetIndex);
					else
						NetworkClient.EvolveCreature(hand, index);
					ResetInteraction();
				}
				else
				{
					Notice("You can only evolve onto one of your own matching-race creatures.");
				}
				break;

			case Mode.SelectBlock:
				break;
		}
	}

	private void OnOppShieldsClicked()
	{
		if (_state is null || _state.IsGameOver)
			return;
		if (_mode == Mode.SelectAttacker && _attackerIndex >= 0)
		{
			NetworkClient.AttackPlayer(_attackerIndex);
			ResetInteraction();
		}
	}

	private void OnMyShieldsClicked(int shieldIndex)
	{
		if (_state is null || _state.IsGameOver)
			return;
		if (_mode == Mode.SelectShieldLook && _state.YourTurn)
		{
			NetworkClient.ActivateTapAbilityShield(_tapCreatureIndex, shieldIndex);
			ResetInteraction();
			return;
		}
		Notice("Online play keeps shields hidden - use a shield-look tap ability to peek.");
	}

	private void ShowGrave(PlayerState player)
	{
		for (var i = _overlayBox.GetChildCount() - 1; i >= 0; i--)
			_overlayBox.GetChild(i).QueueFree();

		_overlayKind = OverlayKind.Look;
		_overlay.Visible = true;
		SetOverlayBoxTitle($"{player.Name}'s graveyard");

		if (player.Graveyard.Count == 0)
		{
			SetOverlayBoxNote("Nothing here yet.");
		}
		else
		{
			var flow = new HFlowContainer();
			flow.AddThemeConstantOverride("h_separation", 8);
			flow.AddThemeConstantOverride("v_separation", 8);
			foreach (var c in player.Graveyard)
			{
				var view = new CardView(CardFor(c), ArtFor(c.CardId));
				if (c.IsTapped)
					view.SnapTapped(true);
				var cc = c;
				view.Clicked += _ => ShowLookAt(cc);
				flow.AddChild(view);
			}
			_overlayBox.AddChild(flow);
		}

		var close = new Button { Text = "Close" };
		close.Pressed += HideOverlay;
		_overlayBox.AddChild(close);
	}

	private void ShowLookAt(CardState? cardState)
	{
		if (cardState is null || cardState.CountOnly)
			return;

		for (var i = _overlayBox.GetChildCount() - 1; i >= 0; i--)
			_overlayBox.GetChild(i).QueueFree();

		_overlayKind = OverlayKind.Look;
		_overlay.Visible = true;

		var card = CardFor(cardState);
		var view = new CardView(card, ArtFor(cardState.CardId));
		view.CustomMinimumSize = new Vector2(240, 335);
		_overlayBox.AddChild(view);

		SetOverlayBoxNote("Click anywhere or press Esc to close.");
	}

	private bool CanBlockWith(CardState candidate)
		=> !candidate.CountOnly
		&& CardFor(candidate) is { } card
		&& card.IsCreature
		&& card.HasKeyword(Keyword.Blocker)
		&& !candidate.IsTapped;

	private bool IsReadyAttacker(CardState candidate)
		=> !candidate.IsTapped && !candidate.IsSummoningSick && CardFor(candidate)?.IsCreature == true;

	private bool IsAttackableByMe(CardState target)
		=> target.IsTapped || For(target)?.HasKeyword(Keyword.CanAttackUntappedCreatures) == true;

	private bool IsCreature(CardState c) => CardFor(c)?.IsCreature == true;

	private Card? For(CardState c) => CardFor(c);

	// --------------------------------------------------------------- render

	private void Refresh()
	{
		if (_state is null)
			return;

		BuildHand(_oppHand, Opp().Hand, faceDown: true);
		BuildShields(_oppShields, Opp().ShieldCount);
		BuildZone(_oppMana, Opp().ManaZone, _prevOppManaTapped, ManaTapStaggerSeconds);
		BuildZone(_oppBattle, Opp().BattleZone);

		BuildShields(_myShields, Me().ShieldCount);
		BuildZone(_myBattle, Me().BattleZone);
		BuildZone(_myMana, Me().ManaZone, _prevMyManaTapped, ManaTapStaggerSeconds);
		BuildHand(_myHand, Me().Hand, faceDown: false);

		_myGrave.Text = $"My graveyard ({Me().Graveyard.Count})";
		_oppGrave.Text = $"Opponent's graveyard ({Opp().Graveyard.Count})";

		if (_state.IsGameOver)
		{
			var winnerName = _state.WinnerId is { Length: > 0 } winSide
				? _state.Players.FirstOrDefault(p => p.Side == winSide)?.Name
				: null;
			_status.Text = winnerName is { Length: > 0 }
				? $"Game over - {winnerName} wins!"
				: "Game over.";
		}
		else if (_state.AttackPending)
		{
			_status.Text = $"{Me().Name} vs {Opp().Name}  |  {(IsPhase("Main") ? "an attack awaits a blocking decision" : _state.Phase)}  |  Turn {_state.TurnNumber}";
		}
		else
		{
			var whoseTurn = _state.YourTurn ? Me().Name : Opp().Name;
			_status.Text =
				$"{whoseTurn}'s turn ({_state.Phase})  |  Turn {_state.TurnNumber}  |  {Me().Name} vs {Opp().Name}";
		}

		_endTurn.Disabled = _state.IsGameOver || !_state.YourTurn || !IsPhase("Main") && !IsPhase("End");
		_rematch.Visible = _state.IsGameOver;
		_rematch.Disabled = false;

		WireHand(_myHand);
		WireBattle(_myBattle, mine: true);
		WireBattle(_oppBattle, mine: false);
		WireShields(_myShields, OnMyShieldsClicked);
		WireZone(_oppShields, OnOppShieldsClicked);

		SyncContextualUi();
		SyncContextualUiScryGate();
		SyncShieldTriggerPopup();
		SyncScryPopup();
		SyncAttackDecisionPopup();
		RefreshFx();
	}

	/// <summary>
	/// Minimal Phase 5 hooks for the state-driven view: a single fanfare when the
	/// game ends and a shatter whenever a player's shield count drops this refresh.
	/// </summary>
	private void RefreshFx()
	{
		if (_state is null)
			return;

		if (_state.IsGameOver)
		{
			if (!_winnerShown)
			{
				_winnerShown = true;
				_fxManager?.BigWin();
				Sfx.Play(SfxId.Win);
			}
			_prevOppShieldCount = Opp().ShieldCount;
			_prevMyShieldCount = Me().ShieldCount;
			return;
		}

		if (_prevOppShieldCount >= 0 && Opp().ShieldCount < _prevOppShieldCount)
		{
			_fxManager?.ShieldShatter(_oppShields.GetGlobalRect().GetCenter(), new Color("ffd25a"));
			Sfx.Play(SfxId.ShieldBreak);
		}
		if (_prevMyShieldCount >= 0 && Me().ShieldCount < _prevMyShieldCount)
		{
			_fxManager?.ShieldShatter(_myShields.GetGlobalRect().GetCenter(), new Color("ffd25a"));
			Sfx.Play(SfxId.ShieldBreak);
		}
		_prevOppShieldCount = Opp().ShieldCount;
		_prevMyShieldCount = Me().ShieldCount;
	}

	private void SyncContextualUi()
	{
		if (_state.IsGameOver)
			return;

		if (_state.AttackPending)
		{
			if (!_state.YourTurn)
			{
				// I am the defender and must choose a blocker or pass.
				_mode = Mode.SelectBlock;
				_attackerIndex = -1;
				ClearActions();
				Prompt($"Choose a blocker ({_state.BlocksAvailable} available), or pass. Esc to cancel.");
				AddAction("Pass (no block)", () =>
				{
					NetworkClient.PassBlock();
					ResetInteraction();
				});
			}
			else
			{
				// I am the attacker, waiting for the defender.
				if (_mode is Mode.SelectAttacker)
					_mode = Mode.Idle;
				ClearActions();
				Prompt("Waiting for the opponent's blocking decision...");
			}
			return;
		}

		if (TriggerWindowIsMine)
		{
			CancelInteraction();
		}
	}

	private bool TriggerWindowIsMine =>
		_state is not null
		&& _state.ShieldTriggerWindowActive
		&& string.Equals(_state.ShieldTriggerOwnerSide, _state.YourSide, StringComparison.Ordinal);

	private void SyncContextualUiScryGate()
	{
		// While the opponent looks at their deck, nothing else may happen - the
		// server enforces this, and this prompt keeps the idle player informed.
		if (_state is null || _state.IsGameOver)
			return;
		if (_state.ScryWindowActive && !ScryWindowIsMine)
		{
			_mode = Mode.Idle;
			ClearActions();
			Prompt("Your opponent is arranging the top of their deck...");
		}
	}

	private void SyncShieldTriggerPopup()
	{
		if (!TriggerWindowIsMine)
		{
			if (_overlayKind == OverlayKind.Trigger)
				HideOverlay();
			return;
		}

		for (var i = _overlayBox.GetChildCount() - 1; i >= 0; i--)
			_overlayBox.GetChild(i).QueueFree();

		_overlayKind = OverlayKind.Trigger;
		_overlay.Visible = true;
		SetOverlayBoxTitle("Shield Trigger!\nYour shields broke");

		var me = Me();
		var hasPlayable = false;
		foreach (var handIndex in _state.PendingTriggerHandIndices)
		{
			if (handIndex < 0 || handIndex >= me.Hand.Count)
				continue;
			var cardState = me.Hand[handIndex];
			var card = CardFor(cardState);
			if (card is null)
				continue;
			if (card.IsEvolution)
			{
				SetOverlayBoxNote($"{card.Name} is an Evolution creature and stays in hand (it can only be evolved onto a creature).");
				continue;
			}
			var playable = !card.Effects.Any(e => e.NeedsTarget) || AnyLegalSpellTarget(card);
			hasPlayable |= playable;
			var idx = handIndex;
			var btn = new Button
			{
				Text = card.Effects.Any(e => e.NeedsTarget) ? $"Play & choose target: {card.Name}" : $"Play {card.Name}",
				Disabled = !playable,
			};
			btn.Pressed += () => PlayPendingTrigger(idx, card);
			_overlayBox.AddChild(btn);
		}

		if (!hasPlayable)
			SetOverlayBoxNote("No effect can target anything right now - only \"leave in hand\" is available.");

		var leave = new Button { Text = "Leave them in hand" };
		leave.Pressed += () =>
		{
			NetworkClient.DeclineShieldTriggers();
			HideOverlay();
			Prompt("Shield triggers left in hand.");
		};
		_overlayBox.AddChild(leave);
	}

	private bool DecisionWindowIsMine =>
		_state is not null
		&& _state.AttackDecisionWindowActive
		&& string.Equals(_state.AttackDecisionOwnerSide, _state.YourSide, System.StringComparison.Ordinal);

	private void SyncAttackDecisionPopup()
	{
		if (_state is null)
			return;
		if (!DecisionWindowIsMine)
		{
			if (_overlayKind == OverlayKind.Decision)
				HideOverlay();
			return;
		}

		for (var i = _overlayBox.GetChildCount() - 1; i >= 0; i--)
			_overlayBox.GetChild(i).QueueFree();

		_overlayKind = OverlayKind.Decision;
		_overlay.Visible = true;
		_decisionShieldPicks.Clear();
		_decisionShieldConfirm = null;

		var attackerCard = CardFor(Me().BattleZone.FirstOrDefault(c => c.AttackDecisionPending)
			?? Me().BattleZone.FirstOrDefault());
		var attackerName = attackerCard?.Name ?? "a creature";
		var kind = _state.AttackDecisionKind;

		switch (kind)
		{
			case "searchToHand":
				SetOverlayBoxTitle($"{attackerName}: you may search your deck");
				SetOverlayBoxNote("The strongest card is taken, then the deck is shuffled.");
				var take = new Button { Text = "Take the card (put it into your hand)" };
				take.Pressed += () =>
				{
					NetworkClient.AttackDecisionAccept(Array.Empty<int>());
					HideOverlay();
				};
				_overlayBox.AddChild(take);
				break;

			case "lookAtShields":
			{
				var need = _state.AttackDecisionShieldCount;
				var defender = Opp();
				SetOverlayBoxTitle($"{attackerName}: you may look at shields");
				SetOverlayBoxNote($"Choose {need} of {defender.Name}'s shields to look at.");

				var toggles = new List<Button>();
				for (var i = 0; i < defender.ShieldCount; i++)
				{
					var shieldIndex = i;
					var shieldToggle = new Button { Text = $"Shield {i + 1}", ToggleMode = true };
					shieldToggle.Toggled += on =>
					{
						if (on)
						{
							if (!_decisionShieldPicks.Contains(shieldIndex))
								_decisionShieldPicks.Add(shieldIndex);
						}
						else
							_decisionShieldPicks.Remove(shieldIndex);
						if (_decisionShieldConfirm is not null)
							_decisionShieldConfirm.Disabled = _decisionShieldPicks.Count != need;
					};
					toggles.Add(shieldToggle);
					_overlayBox.AddChild(shieldToggle);
				}

				var confirm = new Button { Text = $"Look at the {need} picked", Disabled = true };
				_decisionShieldConfirm = confirm;
				confirm.Pressed += () =>
				{
					if (_decisionShieldPicks.Count != need)
						return;
					NetworkClient.AttackDecisionAccept(_decisionShieldPicks.ToArray());
					_decisionShieldPicks.Clear();
					HideOverlay();
					Prompt($"{defender.Name}'s shields revealed.");
				};
				_overlayBox.AddChild(confirm);
				break;
			}

			case "destroyCreature":
			case "destroyPowerAtMost":
				SetOverlayBoxTitle($"{attackerName}: you may destroy a creature");
				if (_state.AttackDecisionTargets.Count == 0)
				{
					SetOverlayBoxNote("There is nothing to destroy.");
				}
				else
				{
					SetOverlayBoxNote("Choose the creature to destroy.");
					foreach (var target in _state.AttackDecisionTargets)
					{
						var btn = new Button { Text = $"Destroy {target.Label}" };
						btn.Pressed += () =>
						{
							NetworkClient.AttackDecisionAcceptTargeted(target.Side, target.Index);
							HideOverlay();
						};
						_overlayBox.AddChild(btn);
					}
				}
				break;

			default:
				SetOverlayBoxTitle($"{attackerName}: resolve the pending effect");
				break;
		}

		var skip = new Button { Text = "Don't use it" };
		skip.Pressed += () =>
		{
			NetworkClient.AttackDecisionDecline();
			HideOverlay();
		};
		_overlayBox.AddChild(skip);
	}

	private bool ScryWindowIsMine =>
		_state is not null
		&& _state.ScryWindowActive
		&& string.Equals(_state.ScryOwnerSide, _state.YourSide, System.StringComparison.Ordinal);

	private void SyncScryPopup()
	{
		if (_state is null)
			return;
		if (!ScryWindowIsMine)
		{
			if (_scryPopup?.Visible == true)
			{
				_scryPopup.Visible = false;
				_scryCards.Clear();
				_scryFingerprint = "";
				_scrySendPending = false;
			}
			return;
		}

		var fingerprint = string.Join(",", _state.ScryCards.Select(c => c.InstanceId));
		if (_scryPopup.Visible && fingerprint == _scryFingerprint)
			return;

		_scryFingerprint = fingerprint;
		_scryCards.Clear();
		_scryCards.AddRange(_state.ScryCards);
		_scrySendPending = false;
		RebuildScryPopup();
		_scryPopup.Visible = true;
	}

	private void RebuildScryPopup()
	{
		for (var i = _scryPopupBox.GetChildCount() - 1; i >= 0; i--)
			_scryPopupBox.GetChild(i).QueueFree();

		var title = new Label
		{
			Text = $"Scry: rearrange the top {_scryCards.Count} cards of your deck",
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		title.AddThemeFontSizeOverride("font_size", 16);
		title.AddThemeColorOverride("font_color", UiStyles.AccentText);
		_scryPopupBox.AddChild(title);

		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 10);
		_scryPopupBox.AddChild(row);

		for (var i = 0; i < _scryCards.Count; i++)
		{
			var slot = new VBoxContainer();
			slot.AddThemeConstantOverride("separation", 4);
			row.AddChild(slot);

			var up = new Button { Text = "Move up" };
			var down = new Button { Text = "Move down" };
			var idx = i;
			up.Pressed += () => SwapScryOrder(idx, idx - 1);
			down.Pressed += () => SwapScryOrder(idx, idx + 1);
			slot.AddChild(up);
			slot.AddChild(down);

			var c = _scryCards[i];
			var view = new CardView(CardFor(c), ArtFor(c.CardId));
			view.CustomMinimumSize = new Vector2(140, 196);
			slot.AddChild(view);
		}

		var note = new Label
		{
			Text = "Leftmost is drawn first - reorder with the buttons, then confirm.",
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		note.AddThemeFontSizeOverride("font_size", 12);
		note.AddThemeColorOverride("font_color", UiStyles.MutedText);
		_scryPopupBox.AddChild(note);

		var buttons = new HBoxContainer();
		buttons.AddThemeConstantOverride("separation", 8);
		buttons.Alignment = BoxContainer.AlignmentMode.Center;
		var confirm = new Button { Text = "Confirm order" };
		confirm.Pressed += () =>
		{
			if (_scrySendPending)
				return;
			_scrySendPending = true;
			NetworkClient.SubmitScryOrder(_scryCards.Select(c => c.InstanceId).ToList());
		};
		var cancel = new Button { Text = "Put back as they were" };
		cancel.Pressed += () =>
		{
			if (_scrySendPending)
				return;
			SubmitIdentityScryOrder();
		};
		buttons.AddChild(confirm);
		buttons.AddChild(cancel);
		_scryPopupBox.AddChild(buttons);
	}

	private void SwapScryOrder(int from, int to)
	{
		if (from < 0 || from >= _scryCards.Count)
			return;
		to = Math.Clamp(to, 0, _scryCards.Count - 1);
		if (to == from)
			return;
		(_scryCards[from], _scryCards[to]) = (_scryCards[to], _scryCards[from]);
		RebuildScryPopup();
	}

	private void SubmitIdentityScryOrder()
	{
		if (_scrySendPending)
			return;
		_scrySendPending = true;
		// The snapshot order is the deck's current top-to-bottom; put it back unchanged.
		NetworkClient.SubmitScryOrder(_state.ScryCards.Select(c => c.InstanceId).ToList());
		Prompt("");
	}

	private void PlayPendingTrigger(int handIndex, Card card)
	{
		if (!card.Effects.Any(e => e.NeedsTarget))
		{
			NetworkClient.PlayShieldTrigger(handIndex);
			HideOverlay();
			return;
		}
		_triggerHandIndex = handIndex;
		_mode = Mode.SelectShieldTarget;
		ClearActions();
		Prompt($"Shield Trigger: choose a target for {card.Name}, or press Esc to leave it in hand.");
	}

	private void BuildZone(VBoxContainer box, List<CardState> zone, List<bool>? prevTappedFlags = null, float staggerSeconds = 0f)
	{
		var flow = GetFlow(box);
		Clear(flow);
		var (sw, sh) = ScaledCardSize();
		for (var i = 0; i < zone.Count; i++)
		{
			var c = zone[i];
			var view = c.CountOnly
				? new CardView(null, faceDown: true)
				: new CardView(CardFor(c), ArtFor(c.CardId));
			view.SetCardSize(sw, sh);
			var wasTapped = prevTappedFlags is not null && i < prevTappedFlags.Count && prevTappedFlags[i];
			if (c.IsTapped != wasTapped)
			{
				// Pose at the old state, then stagger the tap/untap transition so
				// several cards flip visibly one after another (mainly the mana zone).
				if (staggerSeconds > 0f)
					view.AnimateFromTappedAfter(staggerSeconds * i, wasTapped, c.IsTapped);
				else
					view.AnimateFromTapped(wasTapped, c.IsTapped);
			}
			else
				view.SnapTapped(c.IsTapped);
			flow.AddChild(view);
		}
		UpdateTitle(box, zone.Count);
		if (prevTappedFlags is not null)
		{
			prevTappedFlags.Clear();
			prevTappedFlags.AddRange(zone.Select(c => c.IsTapped));
		}
	}

	private void BuildHand(VBoxContainer box, List<CardState> hand, bool faceDown)
	{
		var flow = GetFlow(box);
		Clear(flow);
		var (sw, sh) = ScaledCardSize();
		for (var i = 0; i < hand.Count; i++)
		{
			var c = hand[i];
			var view = faceDown || c.CountOnly
				? new CardView(null, faceDown: true)
				: new CardView(CardFor(c), ArtFor(c.CardId));
			view.SetCardSize(sw, sh);
			flow.AddChild(view);
		}
		UpdateTitle(box, hand.Count);
	}

	private void BuildShields(VBoxContainer box, int count)
	{
		var flow = GetFlow(box);
		Clear(flow);
		var (sw, sh) = ScaledCardSize();
		for (var i = 0; i < count; i++)
		{
			var view = new CardView(null, faceDown: true);
			view.SetCardSize(sw, sh);
			flow.AddChild(view);
		}
		UpdateTitle(box, count);
	}

	private void WireHand(VBoxContainer box)
	{
		var views = GetFlow(box).GetChildren().OfType<CardView>().ToList();
		for (var i = 0; i < views.Count; i++)
		{
			var idx = i;
			views[i].Clicked += _ => OnHandClicked(idx);
		}
	}

	private void WireBattle(VBoxContainer box, bool mine)
	{
		var views = GetFlow(box).GetChildren().OfType<CardView>().ToList();
		for (var i = 0; i < views.Count; i++)
		{
			var idx = i;
			views[i].Clicked += _ => OnBattleClicked(mine, idx);
		}
	}

	private void WireZone(VBoxContainer box, Action onClick)
	{
		foreach (var view in GetFlow(box).GetChildren().OfType<CardView>())
			view.Clicked += _ => onClick();
	}

	private void WireShields(VBoxContainer box, Action<int> onClick)
	{
		var views = GetFlow(box).GetChildren().OfType<CardView>().ToList();
		for (var i = 0; i < views.Count; i++)
		{
			var idx = i;
			views[i].Clicked += _ => onClick(idx);
		}
	}

	// --------------------------------------------------------------- target helpers

	private bool AnyLegalSpellTarget(Card spell)
	{
		foreach (var mine in new[] { false, true })
		{
			var zone = mine ? Me().BattleZone : Opp().BattleZone;
			foreach (var c in zone)
			{
				if (IsCreature(c) && IsLegalSpellTarget(spell, c, mine))
					return true;
			}
		}
		return false;
	}

	private bool AnyLegalOnPlayTarget(Card creature)
	{
		foreach (var mine in new[] { false, true })
		{
			var zone = mine ? Me().BattleZone : Opp().BattleZone;
			foreach (var c in zone)
			{
				if (IsCreature(c) && IsLegalOnPlayTarget(creature, c, mine))
					return true;
			}
		}
		return false;
	}

	private bool IsLegalSpellTarget(Card spell, CardState target, bool targetIsMine)
	{
		var effect = spell.Effects.FirstOrDefault(e => e.NeedsTarget);
		if (effect is null)
			return false;
		var card = CardFor(target);
		if (card is null || !card.IsCreature)
			return false;
		if ((effect.Id is EffectId.Spell_DestroyPowerAtMost or EffectId.OnPlay_DestroyPowerAtMost)
			&& target.Power > effect.Value)
			return false;
		return effect.Target switch
		{
			EffectTargetScope.OwnCreature => targetIsMine,
			EffectTargetScope.OpponentCreature => !targetIsMine,
			_ => true,
		};
	}

	private bool IsLegalOnPlayTarget(Card creature, CardState target, bool targetIsMine)
	{
		var effect = creature.Effects.FirstOrDefault(IsOnPlayTargetedEffect);
		if (effect is null || effect.Target == EffectTargetScope.None)
			return false;
		var card = CardFor(target);
		if (card is null || !card.IsCreature)
			return false;
		if ((effect.Id is EffectId.Spell_DestroyPowerAtMost or EffectId.OnPlay_DestroyPowerAtMost)
			&& target.Power > effect.Value)
			return false;
		return effect.Target switch
		{
			EffectTargetScope.OwnCreature => targetIsMine,
			EffectTargetScope.OpponentCreature => !targetIsMine,
			_ => true,
		};
	}

	private static bool IsOnPlayTargetedEffect(CardEffect e) => e.Id is
		EffectId.OnPlay_TapCreature or
		EffectId.OnPlay_ReturnToHand or
		EffectId.OnPlay_DestroyPowerAtMost or
		EffectId.OnPlay_UntapOwnCreature;

	private bool HasMatchingBase(Card evolution)
	{
		return Me().BattleZone.Any(c =>
			CardFor(c) is { } baseCard && DuelGame.IsEvolutionBase(evolution, baseCard));
	}

	private string SideFor(bool mine) => mine ? Me().Side : Opp().Side;

	// --------------------------------------------------------------- helpers

	private PlayerState Me() => _state.Players.First(p => p.Side == _state.YourSide);
	private PlayerState Opp() => _state.Players.First(p => p.Side != _state.YourSide);
	private bool IsPhase(string phase) => string.Equals(_state.Phase, phase, System.StringComparison.Ordinal);

	private CardState? MyHandCard(int index)
	{
		if (index < 0 || index >= Me().Hand.Count)
			return null;
		return Me().Hand[index];
	}

	private CardState? HandCardAt(int index) => index < 0 || index >= Me().Hand.Count ? null : Me().Hand[index];
	private Card? ForHandCard(int index) => HandCardAt(index) is { } c ? CardFor(c) : null;

	private Card? CardFor(CardState c) =>
		_cardsByCardId.TryGetValue(c.CardId, out var card) ? card : null;

	private string? ArtFor(string cardId) => _artByCardId.TryGetValue(cardId, out var p) ? p : null;

	private void AddAction(string label, Action onClick)
	{
		var b = new Button { Text = label };
		b.Pressed += () =>
		{
			ClearActions();
			_prompt.Text = "";
			onClick();
		};
		_actionBar.AddChild(b);
	}

	private void ClearActions()
	{
		foreach (var child in _actionBar.GetChildren().OfType<Control>().ToList())
			child.QueueFree();
	}

	private void CancelInteraction()
	{
		if (_mode == Mode.Idle && _overlayKind == OverlayKind.None)
			return;
		ResetInteraction();
		if (_overlayKind == OverlayKind.Look)
			HideOverlay();
	}

	private void ResetInteraction()
	{
		_mode = Mode.Idle;
		_attackerIndex = -1;
		_spellHandIndex = -1;
		_triggerHandIndex = -1;
		_summonHandIndex = -1;
		_evolveHandIndex = -1;
		_tapCreatureIndex = -1;
		_pendingEvolveTargetSide = null;
		_pendingEvolveTargetIndex = -1;
		ClearActions();
		_prompt.Text = "";
	}

	private void Prompt(string message)
	{
		_prompt.Text = message;
		_prompt.Modulate = new Color(1f, 0.95f, 0.7f);
	}

	private void Notice(string message)
	{
		_prompt.Text = message;
		_prompt.Modulate = new Color(1f, 0.6f, 0.5f);
	}

	private static HFlowContainer GetFlow(VBoxContainer box)
	{
		foreach (var child in box.GetChildren())
			if (child is HFlowContainer flow)
				return flow;
		var created = new HFlowContainer();
		created.AddThemeConstantOverride("h_separation", 8);
		created.AddThemeConstantOverride("v_separation", 8);
		box.AddChild(created);
		return created;
	}

	private static void Clear(HFlowContainer flow)
	{
		foreach (var child in flow.GetChildren().OfType<Node>().ToList())
			child.QueueFree();
	}

	private static void UpdateTitle(VBoxContainer box, int count)
	{
		foreach (var child in box.GetChildren())
		{
			if (child is Label l)
			{
				var baseName = l.Text.Split("  (").First();
				l.Text = $"{baseName}  ({count})";
			}
		}
	}
}
