using System;
using System.Collections.Generic;
using System.Linq;
using DuelMasters.Core;
using DuelMasters.Domain;
using DuelMasters.Domain.Ai;
using DuelMasters.Gameplay.Audio;
using DuelMasters.Gameplay.CardView;
using DuelMasters.Gameplay.Fx;
using DuelMasters.Resources;
using DuelMasters.UI;
using DuelMasters.UI.Settings;
using Godot;

namespace DuelMasters.Scenes.Arena;

/// <summary>
/// Phase 5: the playable arena. Opens on a deck-selection panel - the player
/// picks a starter deck for themselves and one for the opponent, and chooses
/// whether that opponent is an AI or a second human (hotseat) - then renders the
/// mirrored Duel Masters board (shields / battle zone / mana / hand / deck /
/// graveyard) from the live <see cref="DuelGame"/> state.
///
/// This is a thin presentation layer: every rule check happens in the shared
/// <c>DuelMasters.Domain</c> engine, and the AI opponent is also a domain citizen
/// (<see cref="AiController"/>) driven through the same public API the UI uses.
///
/// Hotseat / fixed-seat model: the bottom row always belongs to Player 1 (the
/// local player in AI duels) and the top row to Player 2 / the AI opponent,
/// whose hand stays face-down unless the "Show AI Cards" debug toggle reveals it.
/// Interactions are side-aware, so hotseat play works from both seats.
/// </summary>
public partial class Arena : Control
{
    private enum Mode { Idle, SelectHand, SelectTarget, SelectBlock, SelectSpellTarget, SelectShieldTarget, SelectSummonTarget, SelectEvolveTarget, EvolveBase, SelectTapTarget, SelectShieldPeek }

    private enum CardSizeKind { Full, Mana, Stack }

    private enum CombatRole { Attack, Target, Block }

    private const string MainMenuPath = "res://src/ui/main_menu/MainMenu.tscn";
    private const float AiStepDelay = 0.55f;

    // When a rule action taps several mana cards at once (summon / spell costs and
    // the start-of-turn untap), animate each card's tap pose one-by-one with this
    // pause between cards so the player can follow the mana being spent. Tunable in
    // the Arena scene inspector.
    [Export] private float ManaTapStaggerSeconds = 2.0f;

    // Card sizing: cards keep a fixed 140x195 aspect, but the on-screen size adapts
    // to the window so nothing ever clips. Widely-adopted "battlefield reflow" like
    // MTG Arena recomputes a base card size from the available board area.
    private const float CardAspect = 195f / 140f; // height / width
    private const float MinCardW = 56f;
    private const float MaxCardW = 200f;
    private const float MinCardH = 90f;
    private const float MaxCardH = 280f;

    private float _cardW = 140f;
    private float _cardH = 195f;
    private float _manaW = 96f;
    private float _manaH = 134f;
    private float _stackW = 88f;
    private float _stackH = 122f;

    private DuelGame _game = null!;
    private readonly Dictionary<string, string> _artByCardId = new();

    private bool _vsAi;
    private AiController? _ai;
    private bool _aiDriving;
    private float _aiTimer;
    private bool _awaitingBlockChoice;
    private int _pendingAiAttackerIndex = -1;

    // Board zones. Seats are fixed: the bottom row always shows Player 1 (the local
    // player in AI duels) and the top row always shows Player 2 / the AI opponent.
    private VBoxContainer _topMana = null!;
    private VBoxContainer _topBattle = null!;
    private VBoxContainer _topHand = null!;
    private VBoxContainer _topShields = null!;
    private VBoxContainer _bottomShields = null!;
    private Control? _topShieldsStrip;
    private Control? _bottomShieldsStrip;
    private VBoxContainer _bottomBattle = null!;
    private VBoxContainer _bottomMana = null!;
    private VBoxContainer _bottomHand = null!;

    private Label _topManaTitle = null!;
    private Label _topBattleTitle = null!;
    private Label _topHandTitle = null!;
    private Label _topShieldsTitle = null!;
    private Label _bottomShieldsTitle = null!;
    private Label _bottomBattleTitle = null!;
    private Label _bottomManaTitle = null!;
    private Label _bottomHandTitle = null!;
    private Label _topDeckLabel = null!;
    private Label _topGraveLabel = null!;
    private Label _bottomDeckLabel = null!;
    private Label _bottomGraveLabel = null!;
    private VBoxContainer _topDeckPile = null!;
    private VBoxContainer _topGravePile = null!;
    private VBoxContainer _bottomDeckPile = null!;
    private VBoxContainer _bottomGravePile = null!;

    private Label _turnLabel = null!;
    private Label _promptLabel = null!;
    private Button _endTurn = null!;
    private Button _takeHitBtn = null!;
    private Button _newDuelBtn = null!;

    // Topmost animation layer: transient "ghost" cards fly deck->hand (draw) or
    // battle->graveyard (destroy). Mouse-transparent so it never eats clicks.
    private Control _fxLayer = null!;

    // Phase 5: the VFX overlay owns flashes, shockwaves, beams, bursts and shake.
    private FxManager _fxManager = null!;

    // Centered "winner" banner shown once when a duel ends.
    private Label _winnerBanner = null!;
    private bool _winnerShown;

    // Pre-action UI state needed to animate a transition after Refresh rebuilds
    // every zone. Battle positions are keyed by CardInstance reference (the engine
    // moves the same instance into the graveyard, so identity survives the action).
    private sealed class FxSnapshot
    {
        public readonly Dictionary<int, int> HandCounts = new();
        public readonly Dictionary<int, int> DeckCounts = new();
        public readonly Dictionary<CardInstance, Vector2> BattlePos = new();
        public readonly Dictionary<int, List<Vector2>> ShieldPos = new();
    }

    private FxSnapshot? _fx;

    // Tap pose of every zone card at the END of the previous refresh. Because the
    // engine mutates instances in place and Refresh() rebuilds all views, a view
    // whose instance tap-state differs from this map animates the change; otherwise
    // it snaps to the current pose (no animation on ordinary refreshes).
    private readonly Dictionary<CardInstance, bool> _prevTapped = new();

    // Graveyard viewer overlay (clicking a grave pile lists every card in it).
    private Control _graveOverlay = null!;
    private Label _graveTitle = null!;
    private VBoxContainer _graveList = null!;

    // Combat role badge tints.
    private static readonly Color AttackTint = new(1f, 0.32f, 0.25f);
    private static readonly Color TargetTint = new(1f, 0.68f, 0.25f);
    private static readonly Color BlockTint = new(0.35f, 0.82f, 1f);

    // Deck selection overlay.
    private Control _selectRoot = null!;
    private OptionButton _myDeckPick = null!;
    private OptionButton _oppDeckPick = null!;
    private OptionButton _oppKindPick = null!;
    private Label _myDeckDesc = null!;
    private Label _oppDeckCaption = null!;
    private Label _oppDeckDesc = null!;
    private Label _selectStatus = null!;
    private readonly List<StarterDeck> _starterDecks = new();

    // Interaction state.
    private Mode _mode = Mode.Idle;
    private int _attackerIndex = -1;
    private bool _selectedHandSide;
    private int _selectedHandIndex = -1;

    // Targeting state for spells cast from hand / from a shield trigger.
    private int _spellHandIndex = -1;
    private int _triggerHandIndex = -1;

    // Targeting state for a creature's on-play ability ("when it enters the battle
    // zone, ...") chosen at summon time.
    private int _summonHandIndex = -1;

    // Targeting state for placing an Evolution creature onto a base creature: the
    // hand slot being evolved, the on-play ability target picked first (if any),
    // and then the battlefield selection of a matching-race base.
    private int _evolveHandIndex = -1;
    private SpellTarget? _pendingEvolveTarget;

    // Targeting state for a creature's Tap Ability. The popup lists the engine's
    // legal target pool, so this only remembers which creature is paying the tap.
    private int _tapCreatureIndex = -1;

    // Multi-target spell selection ("return up to N creatures"): picked targets and
    // the remaining capacity. _maxTargets 0 = the normal single-target flow.
    private int _maxTargets;
    private readonly List<SpellTarget> _spellTargetPicks = new();

    // Hand card popup ("Look at card" / "Play card") shown above the selected card.
    private PanelContainer _handPopup = null!;
    private VBoxContainer _handPopupBox = null!;
    private PanelContainer _lookPopup = null!;
    private VBoxContainer _lookPopupBox = null!;

    // Attack menu ("Look at card" / "Attack" / "Cancel") shown while an attacker is
    // selected in Mode.SelectTarget so the player can inspect the monster or commit
    // to target-picking before swinging.
    private PanelContainer _attackMenu = null!;
    private VBoxContainer _attackMenuBox = null!;

    // Tap-ability target picker popup (lists the engine's legal target pool).
    private PanelContainer _tapMenu = null!;
    private VBoxContainer _tapMenuBox = null!;

    // Shield-trigger decision popup (interrupts the attacker's turn).
    private PanelContainer _triggerPopup = null!;
    private VBoxContainer _triggerPopupBox = null!;
    private string _triggerFingerprint = "";

    // Scry tray (a "look at the top N cards and put them back in any order" tap
    // ability): a popup listing the engine's exposed top-deck cards, each with a
    // move-up / move-down button, plus confirm (submit the new order) and cancel
    // (put them back exactly as they were).
    private PanelContainer _scryPopup = null!;
    private VBoxContainer _scryPopupBox = null!;
    private string _scryFingerprint = "";
    private readonly List<Card> _scryOrder = new();

    // Centered pure-artwork card inspector overlay.
    private Control _inspectOverlay = null!;
    private CenterContainer _inspectCenter = null!;
    private Control? _inspectView;

    public override void _Ready()
    {
        GameSettings.RevealAiHandChanged += HandleRevealChanged;
        BuildLayout();
        ShowDeckSelection();
    }

    public override void _ExitTree()
    {
        GameSettings.RevealAiHandChanged -= HandleRevealChanged;
    }

    private void HandleRevealChanged()
    {
        if (_game is not null)
            Refresh();
    }

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationResized)
            CallDeferred(nameof(HandleResize));
    }

    private void HandleResize()
    {
        RecomputeCardSize();
        Refresh();
    }

    /// <summary>
    /// Derives the on-screen card size from the current window so every zone fits
    /// WITHOUT vertical scrolling. The board is a vertical stack (top→bottom):
    /// header, opponent hand, opponent shields/deck/grave, opponent battle, HUD,
    /// battle, shields/deck/grave, mana, your hand, footer. Card size is computed
    /// from the HEIGHT budget: reserve fixed chrome, divide the remainder across
    /// the fixed-height rows weighted by their typical card size, and let the two
    /// battle rows (SizeFlagsVertical.ExpandFill) absorb any leftover. This keeps
    /// the whole table on screen so every zone is reachable by a real click.
    /// </summary>
    private void RecomputeCardSize()
    {
        var h = Size.Y;
        if (h <= 0f)
            return;

        // Fixed chrome: margins, header, HUD (incl. the Take Hit button), footer, separations.
        const float Chrome = 20f + 46f + 110f + 48f + 84f;
        var avail = Mathf.Max(160f, h - Chrome);

        // Fixed-height rows in units of _cardH after weighting by each row's card type:
        //   oppHand(card) + oppShields(stack) + oppMana(mana)
        //   + bottomShields(stack) + mana(mana) + yourHand(card)  =  c(2) + stack(2) + mana(2)
        // with stack≈0.62c and mana≈0.9c the fixed sum ≈ 5.04c; the two battle rows
        // (flex, stretch ratio 1.55) claim the remaining room. A divisor of 8.5 keeps the
        // minimum of every row plus its title inside the window; the flex battle rows then
        // stretch to absorb all leftover height so the table always fills the board.
        var byHeight = Mathf.Clamp(avail / 8.5f, MinCardH, MaxCardH);

        _cardH = byHeight;
        _cardW = _cardH / CardAspect;
        // Mana zone cards are a little wider than the tight shields/deck stacks so the
        // mana number stays readable; stacks are the compact face-down piles.
        _manaH = _cardH * 0.9f;
        _manaW = _manaH / CardAspect;
        _stackH = _cardH * 0.62f;
        _stackW = _stackH / CardAspect;
    }

    // ------------------------------------------------------------ initialization

    private void ShowDeckSelection()
    {
        ResetInteraction();
        _aiDriving = false;
        _awaitingBlockChoice = false;
        _game = null!;
        _ai = null;
        _fx = null;
        _prevTapped.Clear();
        _winnerShown = false;
        if (_winnerBanner != null)
            _winnerBanner.Visible = false;
        _selectRoot.Visible = true;
        _turnLabel.Text = "";
        _promptLabel.Text = "";
        Refresh();
    }

    private void OnStartDuel()
    {
        var myDeck = StarterDecks.ResolveCards(SelectedDeckId(_myDeckPick));
        var oppDeck = StarterDecks.ResolveCards(SelectedDeckId(_oppDeckPick));
        _vsAi = _oppKindPick.GetSelectedId() == 0;

        var bottom = new Player(_vsAi ? "You" : "Player 1", myDeck);
        var top = new Player(_vsAi ? "AI" : "Player 2", oppDeck);

        _artByCardId.Clear();
        foreach (var r in CardCatalog.Load())
            _artByCardId[r.Card.Id] = r.ImagePath;

        _game = new DuelGame(bottom, top);
        _ai = _vsAi ? new AiController(top, AiProfile.Standard) : null;
        _fx = null;
        _prevTapped.Clear();

        try
        {
            _game.StartGame(shuffle: true);
            _game.StartTurn();
            _game.Draw();

            _selectRoot.Visible = false;
            ResetInteraction();
            Refresh();

            // The human (Player 1) always opens; the AI takes over after the
            // first human pass.
        }
        catch (RuleViolationException ex)
        {
            _selectStatus.Text = $"Could not start: {ex.Message}";
            _selectStatus.Modulate = UiStyles.ErrorText;
        }
    }

    private string SelectedDeckId(OptionButton pick)
    {
        if (_starterDecks.Count == 0)
            return "";
        var i = pick.Selected < 0 ? 0 : pick.Selected;
        if (i >= _starterDecks.Count)
            i = _starterDecks.Count - 1;
        return _starterDecks[i].Id;
    }

    // ------------------------------------------------------------------- layout

    private void BuildLayout()
    {
        // Table backdrop behind the whole board.
        var backdrop = new Panel();
        backdrop.SetAnchorsPreset(LayoutPreset.FullRect);
        backdrop.AddThemeStyleboxOverride("panel", TableBackdrop());
        AddChild(backdrop);

        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        AddChild(margin);

        // The board fills the window: the root VBox stretches its flex rows across the
        // whole arena height so there is never a dead band of unused space. Every flex
        // row carries a stretch ratio (hand 1.1 / band 1.0 / mana 1.05 / battle 1.55,
        // mirrored top and bottom) so battle space dominates near the table centre.
        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 8);
        root.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        root.SizeFlagsVertical = SizeFlags.ExpandFill;
        margin.AddChild(root);

        // Animation layer floats above the whole board but below the popups/overlays.
        _fxLayer = new Control { Name = "FxLayer", MouseFilter = Control.MouseFilterEnum.Ignore };
        _fxLayer.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_fxLayer);

        // Phase 5: VFX + SFX overlay (flashes, shockwaves, beams, bursts, shake).
        _fxManager = new FxManager();
        _fxLayer.AddChild(_fxManager);

        // Centered winner banner, faded in when the duel ends.
        _winnerBanner = new Label
        {
            Visible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Text = "",
        };
        _winnerBanner.SetAnchorsPreset(LayoutPreset.Center);
        _winnerBanner.AddThemeFontSizeOverride("font_size", 46);
        _winnerBanner.AddThemeColorOverride("font_color", new Color("ffd25a"));
        _winnerBanner.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.85f));
        _winnerBanner.AddThemeConstantOverride("outline_size", 10);
        _fxLayer.AddChild(_winnerBanner);

        // Header.
        var header = new HBoxContainer();
        root.AddChild(header);

        var menuBtn = new Button { Text = "< Main Menu" };
        menuBtn.Pressed += OnBackToMenu;
        header.AddChild(menuBtn);

        header.AddChild(new Control { CustomMinimumSize = new Vector2(12, 0) });

        var title = new Label { Text = "ARENA" };
        title.AddThemeFontSizeOverride("font_size", 22);
        title.AddThemeColorOverride("font_color", UiStyles.TitleText);
        header.AddChild(title);

        header.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        _newDuelBtn = new Button { Text = "New Duel" };
        _newDuelBtn.Pressed += ShowDeckSelection;
        header.AddChild(_newDuelBtn);

        // Reserve the top-right corner occupied by the SceneOptionsMenu gear (44x44
        // inset 16px from the edge) so the button never slides underneath it.
        header.AddChild(new Control { CustomMinimumSize = new Vector2(60, 0) });

        // =====================================================================
        // DM board architecture (top → bottom), mirrored for each player.
        //   Opponent (outer→inner): hand, shields+deck/grave, mana, BATTLE
        //   [HUD]
        //   You (inner→outer): BATTLE, shields+deck/grave, mana, hand
        // Both battle zones face each other across the HUD, exactly as in the game.
        // The two battle rows are SizeFlagsVertical.ExpandFill so they absorb the
        // leftover height; every other row is sized from RecomputeCardSize, which
        // guarantees the whole stack fits the window with no vertical scrolling.
        // =====================================================================

        // ---- Opponent: hand (outer edge) ----
        var oppHandRow = new HBoxContainer();
        oppHandRow.Alignment = BoxContainer.AlignmentMode.Center;
        oppHandRow.AddThemeConstantOverride("separation", 18);
        oppHandRow.SizeFlagsVertical = SizeFlags.ExpandFill;
        oppHandRow.SizeFlagsStretchRatio = 1.1f;
        root.AddChild(oppHandRow);
        _topHand = BuildZone(Civilization.Zero, "OPP HAND", out _topHandTitle);
        _topHand.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        oppHandRow.AddChild(_topHand);

        // ---- Opponent: shields / deck / graveyard band ----
        var oppBoardBand = new HBoxContainer();
        oppBoardBand.Alignment = BoxContainer.AlignmentMode.Center;
        oppBoardBand.AddThemeConstantOverride("separation", 18);
        oppBoardBand.SizeFlagsVertical = SizeFlags.ExpandFill;
        oppBoardBand.SizeFlagsStretchRatio = 1.0f;
        root.AddChild(oppBoardBand);
        _topShields = BuildZone(Civilization.Zero, "SHIELDS", out _topShieldsTitle);
        _topShields.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        oppBoardBand.AddChild(_topShields);
        BuildPile(oppBoardBand, isTop: true, isDeck: true, out _topDeckPile, out _topDeckLabel);
        BuildPile(oppBoardBand, isTop: true, isDeck: false, out _topGravePile, out _topGraveLabel);

        // ---- Opponent: mana (outer edge, nearest their hand) ----
        var oppManaRow = new HBoxContainer();
        oppManaRow.Alignment = BoxContainer.AlignmentMode.Center;
        oppManaRow.AddThemeConstantOverride("separation", 18);
        oppManaRow.SizeFlagsVertical = SizeFlags.ExpandFill;
        oppManaRow.SizeFlagsStretchRatio = 1.05f;
        root.AddChild(oppManaRow);
        _topMana = BuildZone(Civilization.Zero, "OPP MANA", out _topManaTitle);
        _topMana.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        oppManaRow.AddChild(_topMana);

        // ---- Opponent battle zone (inner, adjacent to center) ----
        _topBattle = BuildZone(Civilization.Zero, "OPP BATTLE", out _topBattleTitle);
        _topBattle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _topBattle.SizeFlagsVertical = SizeFlags.ExpandFill;
        _topBattle.SizeFlagsStretchRatio = 1.55f;
        root.AddChild(_topBattle);

        // Center HUD.
        var hud = new PanelContainer();
        hud.AddThemeStyleboxOverride("panel", HudPanel());
        root.AddChild(hud);

        var hudBox = new VBoxContainer();
        hudBox.AddThemeConstantOverride("separation", 2);
        hud.AddChild(hudBox);

        _turnLabel = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
        _turnLabel.AddThemeFontSizeOverride("font_size", 18);
        _turnLabel.AddThemeColorOverride("font_color", UiStyles.AccentText);
        hudBox.AddChild(_turnLabel);

        _promptLabel = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _promptLabel.AddThemeFontSizeOverride("font_size", 14);
        _promptLabel.AddThemeColorOverride("font_color", UiStyles.BodyText);
        hudBox.AddChild(_promptLabel);

        _takeHitBtn = new Button { Text = "Take Hit / Pass", Visible = false };
        _takeHitBtn.Pressed += TakeHit;
        _takeHitBtn.Alignment = HorizontalAlignment.Center;
        hudBox.AddChild(_takeHitBtn);

        // ---- Your battle zone (inner, adjacent to center) ----
        _bottomBattle = BuildZone(Civilization.Zero, "BATTLE ZONE", out _bottomBattleTitle);
        _bottomBattle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _bottomBattle.SizeFlagsVertical = SizeFlags.ExpandFill;
        _bottomBattle.SizeFlagsStretchRatio = 1.55f;
        root.AddChild(_bottomBattle);

        // ---- Your shields / deck / graveyard band ----
        var yourBoardBand = new HBoxContainer();
        yourBoardBand.Alignment = BoxContainer.AlignmentMode.Center;
        yourBoardBand.AddThemeConstantOverride("separation", 18);
        yourBoardBand.SizeFlagsVertical = SizeFlags.ExpandFill;
        yourBoardBand.SizeFlagsStretchRatio = 1.0f;
        root.AddChild(yourBoardBand);
        _bottomShields = BuildZone(Civilization.Zero, "SHIELDS", out _bottomShieldsTitle);
        _bottomShields.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        yourBoardBand.AddChild(_bottomShields);
        BuildPile(yourBoardBand, isTop: false, isDeck: true, out _bottomDeckPile, out _bottomDeckLabel);
        BuildPile(yourBoardBand, isTop: false, isDeck: false, out _bottomGravePile, out _bottomGraveLabel);

        // ---- Your mana (outer edge, nearest you) ----
        var yourManaRow = new HBoxContainer();
        yourManaRow.Alignment = BoxContainer.AlignmentMode.Center;
        yourManaRow.AddThemeConstantOverride("separation", 18);
        yourManaRow.SizeFlagsVertical = SizeFlags.ExpandFill;
        yourManaRow.SizeFlagsStretchRatio = 1.05f;
        root.AddChild(yourManaRow);
        _bottomMana = BuildZone(Civilization.Zero, "MANA", out _bottomManaTitle);
        _bottomMana.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        yourManaRow.AddChild(_bottomMana);

        // ---- Your hand (outer edge, closest) ----
        var handRow = new VBoxContainer();
        handRow.AddThemeConstantOverride("separation", 4);
        handRow.SizeFlagsVertical = SizeFlags.ExpandFill;
        handRow.SizeFlagsStretchRatio = 1.1f;
        root.AddChild(handRow);

        _bottomHandTitle = new Label { Text = "YOUR HAND", HorizontalAlignment = HorizontalAlignment.Center };
        _bottomHandTitle.AddThemeFontSizeOverride("font_size", 13);
        _bottomHandTitle.AddThemeColorOverride("font_color", UiStyles.BodyText);

        _bottomHand = new VBoxContainer();
        _bottomHand.AddThemeConstantOverride("separation", 2);
        _bottomHand.SizeFlagsVertical = SizeFlags.ExpandFill;
        handRow.AddChild(_bottomHand);
        _bottomHand.AddChild(_bottomHandTitle);

        var flow = new HBoxContainer();
        flow.Alignment = BoxContainer.AlignmentMode.Center;
        flow.AddThemeConstantOverride("separation", 8);
        flow.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        flow.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _bottomHand.AddChild(CenteredFlowHost(flow));

        // Footer.
        var footer = new HBoxContainer();
        footer.AddThemeConstantOverride("separation", 16);
        root.AddChild(footer);

        _endTurn = new Button { Text = "End Turn" };
        _endTurn.Pressed += OnEndTurn;
        footer.AddChild(_endTurn);

        footer.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        var hint = new Label { Text = "Click a hand card to select it, then play/look. Click a mana card to mark it used this turn. Click a ready creature to attack. Click any other field card to look at it." };
        hint.AddThemeFontSizeOverride("font_size", 13);
        hint.AddThemeColorOverride("font_color", UiStyles.MutedText);
        footer.AddChild(hint);

        // Top-right options gear.
        AddChild(new SceneOptionsMenu { ShowBackToMenu = true });

        BuildHandPopup();
        BuildLookPopup();
        BuildAttackMenu();
        BuildTapMenu();
        BuildShieldTriggerPopup();
        BuildScryPopup();
        BuildGraveyardOverlay();
        BuildInspectOverlay();

        BuildDeckSelection();
    }

    // ------------------------------------------------------------- hand popup

    private void BuildHandPopup()
    {
        _handPopup = new PanelContainer();
        _handPopup.AddThemeStyleboxOverride("panel", UiStyles.ModalCard());
        _handPopup.Visible = false;
        AddChild(_handPopup);

        _handPopupBox = new VBoxContainer();
        _handPopupBox.AddThemeConstantOverride("separation", 6);
        _handPopupBox.CustomMinimumSize = new Vector2(230, 0);
        _handPopup.AddChild(_handPopupBox);
    }

    private void ShowHandPopup(bool isBottomSide, int index)
    {
        var player = _game!.ActivePlayer;
        var instance = player.Hand[index];
        var card = instance.Card;

        HideLookPopup();

        foreach (var child in _handPopupBox.GetChildren().OfType<Control>().ToList())
            child.QueueFree();

        var title = new Label { Text = $"{card.Name}\n{DescribeCard(card)}", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        title.CustomMinimumSize = new Vector2(230, 0);
        title.AddThemeFontSizeOverride("font_size", 14);
        title.AddThemeColorOverride("font_color", CivilizationPalette.Color(card.Civilization).Lightened(0.25f));
        title.HorizontalAlignment = HorizontalAlignment.Center;
        _handPopupBox.AddChild(title);

        var look = new Button { Text = "Look at card" };
        look.Pressed += () => ShowInspect(card);
        _handPopupBox.AddChild(look);

        if (!_game.ManaChargedThisTurn)
        {
            var charge = new Button { Text = "Charge Mana" };
            charge.Pressed += () => DoCharge(index);
            _handPopupBox.AddChild(charge);
        }

        if (card.IsEvolution && !_game.HasAttackedThisTurn)
        {
            var evolve = new Button
            {
                Text = $"Evolve onto a {card.EvolutionOf} creature",
                Disabled = !_game.CanEvolve(player, card),
            };
            evolve.Pressed += () => DoEvolve(index);
            _handPopupBox.AddChild(evolve);

            if (DuelGame.HasOnPlayTargetChoice(card))
            {
                var use = new Button
                {
                    Text = "Evolve & use ability",
                    Disabled = !_game.CanEvolve(player, card) || !AnyLegalOnPlayTarget(card),
                };
                use.Pressed += () => DoEvolveTargeted(index);
                _handPopupBox.AddChild(use);
            }
            var free = new Label
            {
                Text = $"Free - no mana. Put it on top of one of your {card.EvolutionOf} creatures (it inherits no power; the stack counts as the top card).",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(230, 0),
            };
            free.AddThemeFontSizeOverride("font_size", 11);
            _handPopupBox.AddChild(free);
        }
        else if (card.IsCreature && !_game.HasAttackedThisTurn)
        {
            var summon = new Button { Text = "Summon", Disabled = !_game.CanSummon(player, card) };
            summon.Pressed += () => DoSummon(index);
            _handPopupBox.AddChild(summon);

            if (DuelGame.HasOnPlayTargetChoice(card))
            {
                var use = new Button
                {
                    Text = "Summon & use ability",
                    Disabled = !_game.CanSummon(player, card) || !AnyLegalOnPlayTarget(card),
                };
                use.Pressed += () => DoSummonTargeted(index);
                _handPopupBox.AddChild(use);
            }
        }
        else if (card.CardType == CardType.Spell && !_game.HasAttackedThisTurn)
        {
            var cast = new Button { Text = "Cast", Disabled = !_game.CanPlay(player, card) };
            cast.Pressed += () => DoCast(index);
            _handPopupBox.AddChild(cast);
        }

        _handPopup.Visible = true;
        CallDeferred(nameof(PositionHandPopup));
    }

    private void PositionHandPopup()
    {
        if (_handPopup is null || !_handPopup.Visible)
            return;
        PositionPopupAtLeftSide(_handPopup);
    }

    private void PositionPopupAtLeftSide(PanelContainer popup)
    {
        var size = popup.GetCombinedMinimumSize();
        var viewport = GetViewportRect().Size;
        // Center the decision panel in the LEFT side of the screen (quarter point),
        // keeping it compact instead of spanning the whole left edge.
        var pos = new Vector2(
            viewport.X * 0.25f - size.X / 2f,
            viewport.Y * 0.5f - size.Y / 2f);
        pos.X = Mathf.Clamp(pos.X, 8f, Mathf.Max(8f, viewport.X - size.X - 8f));
        pos.Y = Mathf.Clamp(pos.Y, 8f, Mathf.Max(8f, viewport.Y - size.Y - 8f));
        popup.SetGlobalPosition(pos);
    }

    private void HideHandPopup()
    {
        _handPopup.Visible = false;
    }

    private bool AnyLegalOnPlayTarget(Card creature)
    {
        if (_game is null)
            return false;
        var me = _game.ActivePlayer;
        var foe = _game.Opponent;
        for (var i = 0; i < me.BattleZone.Count; i++)
            if (_game.IsLegalOnPlayTarget(creature, me, me, i))
                return true;
        for (var i = 0; i < foe.BattleZone.Count; i++)
            if (_game.IsLegalOnPlayTarget(creature, me, foe, i))
                return true;
        return false;
    }

    private void ShowSpellTargetConfirm(Card spell)
    {
        HideLookPopup();
        UpdateSpellTargetConfirm(spell);
    }

    private void UpdateSpellTargetConfirm(Card spell)
    {
        foreach (var child in _handPopupBox.GetChildren().OfType<Control>().ToList())
            child.QueueFree();

        var title = new Label { Text = $"{spell.Name}\nChoose up to {_maxTargets} creatures", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        title.CustomMinimumSize = new Vector2(230, 0);
        title.AddThemeFontSizeOverride("font_size", 14);
        title.AddThemeColorOverride("font_color", CivilizationPalette.Color(spell.Civilization).Lightened(0.25f));
        title.HorizontalAlignment = HorizontalAlignment.Center;
        _handPopupBox.AddChild(title);

        var list = _spellTargetPicks.Count == 0
            ? "No targets selected."
            : string.Join("\n", _spellTargetPicks.Select(p => $"• {p.Owner.BattleZone[p.Index].Card.Name}"));
        var summary = new Label { Text = $"{list}\nSelected: {_spellTargetPicks.Count}/{_maxTargets}", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        summary.CustomMinimumSize = new Vector2(230, 0);
        summary.AddThemeFontSizeOverride("font_size", 12);
        _handPopupBox.AddChild(summary);

        var cast = new Button { Text = "Cast spell", Disabled = _spellTargetPicks.Count == 0 };
        cast.Pressed += ConfirmCastSpellTargets;
        _handPopupBox.AddChild(cast);

        var cancel = new Button { Text = "Cancel" };
        cancel.Pressed += () =>
        {
            ResetInteraction();
            Refresh();
        };
        _handPopupBox.AddChild(cancel);

        _handPopup.Visible = true;
        CallDeferred(nameof(PositionHandPopup));
    }

    private void ConfirmCastSpellTargets()
    {
        if (_spellHandIndex < 0 || _spellTargetPicks.Count == 0)
            return;
        var hand = _spellHandIndex;
        var picks = new List<SpellTarget>(_spellTargetPicks);
        Safe(() =>
        {
            var spell = _game.ActivePlayer.Hand[hand].Card;
            _game.CastSpell(hand, picks);
            PlayCastFx(spell);
        });
    }

    // --------------------------------------------------------- look popup
    // A lightweight "Look at card" popup used when clicking an on-board card
    // that has no available game action (enemy creatures, tapped/sick creatures,
    // opponent mana, anything during the opponent's turn). It never enters the
    // hand-selection state - it only offers inspection.

    private void BuildLookPopup()
    {
        _lookPopup = new PanelContainer();
        _lookPopup.AddThemeStyleboxOverride("panel", UiStyles.ModalCard());
        _lookPopup.Visible = false;
        AddChild(_lookPopup);

        _lookPopupBox = new VBoxContainer();
        _lookPopupBox.AddThemeConstantOverride("separation", 6);
        _lookPopupBox.CustomMinimumSize = new Vector2(230, 0);
        _lookPopup.AddChild(_lookPopupBox);
    }

    private void ShowLookPopup(Card card)
    {
        if (card is null)
            return;
        HideHandPopup();
        HideAttackMenu();

        foreach (var child in _lookPopupBox.GetChildren().OfType<Control>().ToList())
            child.QueueFree();

        var title = new Label { Text = $"{card.Name}\n{DescribeCard(card)}", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        title.CustomMinimumSize = new Vector2(230, 0);
        title.AddThemeFontSizeOverride("font_size", 14);
        title.AddThemeColorOverride("font_color", CivilizationPalette.Color(card.Civilization).Lightened(0.25f));
        title.HorizontalAlignment = HorizontalAlignment.Center;
        _lookPopupBox.AddChild(title);

        var look = new Button { Text = "Look at card" };
        look.Pressed += () => ShowInspect(card);
        _lookPopupBox.AddChild(look);

        _lookPopup.Visible = true;
        CallDeferred(nameof(PositionLookPopup));
    }

    private void PositionLookPopup()
    {
        if (_lookPopup is null || !_lookPopup.Visible)
            return;
        PositionPopupAtLeftSide(_lookPopup);
    }

    private void HideLookPopup()
    {
        _lookPopup.Visible = false;
    }

    // ----------------------------------------------------------- attack menu
    // Shown as soon as the player selects a battle-zone creature to attack with,
    // and re-opened by clicking it again. Offers: inspect the monster, start
    // picking an enemy target, or drop the selection.

    private void BuildAttackMenu()
    {
        _attackMenu = new PanelContainer();
        _attackMenu.AddThemeStyleboxOverride("panel", UiStyles.ModalCard());
        _attackMenu.Visible = false;
        AddChild(_attackMenu);

        _attackMenuBox = new VBoxContainer();
        _attackMenuBox.AddThemeConstantOverride("separation", 6);
        _attackMenuBox.CustomMinimumSize = new Vector2(230, 0);
        _attackMenu.AddChild(_attackMenuBox);
    }

    private void ShowAttackMenu(Card card)
    {
        if (card is null)
            return;
        HideHandPopup();
        HideLookPopup();

        foreach (var child in _attackMenuBox.GetChildren().OfType<Control>().ToList())
            child.QueueFree();

        var title = new Label { Text = $"{card.Name}\nChoose how to proceed", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        title.CustomMinimumSize = new Vector2(230, 0);
        title.AddThemeFontSizeOverride("font_size", 14);
        title.AddThemeColorOverride("font_color", CivilizationPalette.Color(card.Civilization).Lightened(0.25f));
        title.HorizontalAlignment = HorizontalAlignment.Center;
        _attackMenuBox.AddChild(title);

        var look = new Button { Text = "Look at card" };
        look.Pressed += () => ShowInspect(card);
        _attackMenuBox.AddChild(look);

        var attack = new Button { Text = "Attack" };
        attack.Pressed += () =>
        {
            HideAttackMenu();
            var name = _game is not null && _attackerIndex >= 0 && _attackerIndex < _game.ActivePlayer.BattleZone.Count
                ? _game.ActivePlayer.BattleZone[_attackerIndex].Card.Name
                : card.Name;
            Prompt($"Pick a target for {name}: a tapped enemy creature, or the enemy shields.");
        };
        _attackMenuBox.AddChild(attack);

        // A ready creature that has not attacked yet may pay a tap to resolve one of
        // its Tap Abilities instead of (or before) attacking.
        if (_game is not null && _game.CanUseTapAbility(_game.ActivePlayer, _attackerIndex))
        {
            var use = new Button { Text = "Use tap ability" };
            use.Pressed += () => UseTapAbility(_attackerIndex);
            _attackMenuBox.AddChild(use);
        }

        var cancel = new Button { Text = "Cancel" };
        cancel.Pressed += () =>
        {
            ResetInteraction();
            Prompt("Attack cancelled - pick a creature to attack when ready.");
            Refresh();
        };
        _attackMenuBox.AddChild(cancel);

        _attackMenu.Visible = true;
        CallDeferred(nameof(PositionAttackMenu));
    }

    private void PositionAttackMenu()
    {
        if (_attackMenu is null || !_attackMenu.Visible)
            return;
        PositionPopupAtLeftSide(_attackMenu);
    }

    private void HideAttackMenu()
    {
        if (_attackMenu is not null)
            _attackMenu.Visible = false;
    }

    // ---------------------------------------------------------- tap ability menu
    // "Use tap ability" resolves a ready creature's (first) Tap Ability. Global
    // abilities (draw, charge, discard, civ-wide grants) fire immediately; targeted
    // ones open a popup listing the engine's legal target pool, so the player never
    // has to remember power caps / civilization / type filters to aim correctly.

    private void BuildTapMenu()
    {
        _tapMenu = new PanelContainer();
        _tapMenu.AddThemeStyleboxOverride("panel", UiStyles.ModalCard());
        _tapMenu.Visible = false;
        AddChild(_tapMenu);

        _tapMenuBox = new VBoxContainer();
        _tapMenuBox.AddThemeConstantOverride("separation", 6);
        _tapMenuBox.CustomMinimumSize = new Vector2(300, 0);
        _tapMenu.AddChild(_tapMenuBox);
    }

    private void UseTapAbility(int creatureIndex)
    {
        HideAttackMenu();
        if (_game is null || creatureIndex < 0 || creatureIndex >= _game.ActivePlayer.BattleZone.Count)
            return;
        var creature = _game.ActivePlayer.BattleZone[creatureIndex];
        if (creature.Card.TapAbilities.Any(e => e.Id == EffectId.Tap_ChooseShieldLook))
        {
            // "Choose a shield and look at it, then put it back where it was": enter
            // a pick mode where the next click on the player's own shield cards
            // peeks at the chosen shield (the shield itself never moves).
            _tapCreatureIndex = creatureIndex;
            _mode = Mode.SelectShieldPeek;
            HideHandPopup();
            HideLookPopup();
            HideTapTargetMenu();
            Prompt($"Choose a shield to look at: click one of YOUR face-down shields. It stays exactly where it was.");
            Refresh();
            return;
        }
        if (creature.Card.TapAbilities.Any(e => e.Id == EffectId.Tap_ScryTopCards))
        {
            ResetInteraction();
            Safe(() =>
            {
                _game.ActivateTapAbilityScry(creatureIndex);
                PlayTapFx(creature);
            });
            // The engine now holds the scry window open; the tray popup comes back
            // through SyncScryPopup on the next Refresh.
            Prompt("Look at the top cards of your deck, order them, then confirm the order.");
            Refresh();
            return;
        }
        var raceEffect = creature.Card.TapAbilities.FirstOrDefault(e => e.Id is
            EffectId.Tap_ChooseRaceUntapEot or
            EffectId.Tap_ChooseRaceGrantSlayerEot or
            EffectId.Tap_ChooseRaceToHandEot or
            EffectId.Tap_ChooseRaceMustAttackPowerAttackerEot or
            EffectId.Tap_ChooseRaceUnblockableByPowerEot);
        if (raceEffect is not null)
        {
            ShowTapRaceMenu(creatureIndex, raceEffect);
            return;
        }
        var targeted = creature.Card.TapAbilities.FirstOrDefault(e => e.Target != EffectTargetScope.None);
        if (targeted is null)
        {
            Safe(() =>
            {
                _game.ActivateTapAbility(creatureIndex);
                PlayTapFx(creature);
            });
            Prompt($"{creature.Card.Name} used its tap ability.");
            return;
        }
        ShowTapTargetMenu(creatureIndex, targeted);
    }

    private void ShowTapRaceMenu(int creatureIndex, CardEffect eff)
    {
        HideAttackMenu();
        HideHandPopup();
        HideLookPopup();

        foreach (var child in _tapMenuBox.GetChildren().OfType<Control>().ToList())
            child.QueueFree();

        var creature = _game!.ActivePlayer.BattleZone[creatureIndex];
        var title = new Label
        {
            Text = $"{creature.Card.Name}: choose a race",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        title.CustomMinimumSize = new Vector2(300, 0);
        title.AddThemeFontSizeOverride("font_size", 14);
        title.AddThemeColorOverride("font_color", CivilizationPalette.Color(creature.Card.Civilization).Lightened(0.25f));
        title.HorizontalAlignment = HorizontalAlignment.Center;
        _tapMenuBox.AddChild(title);

        foreach (var race in _game.LegalRaceChoices(_game.ActivePlayer))
        {
            var pick = new Button { Text = race };
            pick.Pressed += () =>
            {
                Safe(() =>
                {
                    _game.ActivateTapAbility(creatureIndex, null, race);
                    PlayTapFx(creature);
                });
                Prompt($"{creature.Card.Name} used its tap ability ({race}).");
            };
            _tapMenuBox.AddChild(pick);
        }

        var cancel = new Button { Text = "Cancel" };
        cancel.Pressed += () =>
        {
            HideTapTargetMenu();
            ResetInteraction();
            Prompt("Tap ability cancelled - pick a creature to attack when ready.");
            Refresh();
        };
        _tapMenuBox.AddChild(cancel);

        _tapMenu.Visible = true;
        CallDeferred(nameof(PositionTapMenu));
    }

    private void ShowTapTargetMenu(int creatureIndex, CardEffect eff)
    {
        HideAttackMenu();
        HideHandPopup();
        HideLookPopup();

        foreach (var child in _tapMenuBox.GetChildren().OfType<Control>().ToList())
            child.QueueFree();

        var creature = _game!.ActivePlayer.BattleZone[creatureIndex];
        var title = new Label
        {
            Text = $"{creature.Card.Name}: choose the tap-ability target",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        title.CustomMinimumSize = new Vector2(300, 0);
        title.AddThemeFontSizeOverride("font_size", 14);
        title.AddThemeColorOverride("font_color", CivilizationPalette.Color(creature.Card.Civilization).Lightened(0.25f));
        title.HorizontalAlignment = HorizontalAlignment.Center;
        _tapMenuBox.AddChild(title);

        foreach (var item in _game.TapTargetPool(_game.ActivePlayer, eff))
        {
            var location = TapTargetLocation(_game, item);
            if (location is not (var owner, var zone, var index))
                continue;
            var pick = new Button
            {
                Text = $"{zone} - {item.Card.Name} ({item.Card.Power} power)...",
                TooltipText = DescribeCard(item.Card),
            };
            var tapIdx = creatureIndex;
            pick.Pressed += () =>
            {
                Safe(() =>
                {
                    _game.ActivateTapAbility(tapIdx, new[] { new SpellTarget(owner, index) });
                    PlayTapFx(creature);
                });
                Prompt($"{creature.Card.Name} used its tap ability.");
            };
            _tapMenuBox.AddChild(pick);
        }

        var cancel = new Button { Text = "Cancel" };
        cancel.Pressed += () =>
        {
            HideTapTargetMenu();
            ResetInteraction();
            Prompt("Tap ability cancelled - pick a creature to attack when ready.");
            Refresh();
        };
        _tapMenuBox.AddChild(cancel);

        _tapMenu.Visible = true;
        CallDeferred(nameof(PositionTapMenu));
    }

    private void PositionTapMenu()
    {
        if (_tapMenu is null || !_tapMenu.Visible)
            return;
        PositionPopupAtLeftSide(_tapMenu);
    }

    private void HideTapTargetMenu()
    {
        if (_tapMenu is not null)
            _tapMenu.Visible = false;
    }

    /// <summary>
    /// Map a tap-ability pool card back to the player and engine-zone index the
    /// engine expects when activating the ability (battle zone / mana zone /
    /// graveyard of either player). Returns null when the card left its zone.
    /// </summary>
    private static (Player Owner, string Zone, int Index)? TapTargetLocation(DuelGame game, CardInstance instance)
    {
        if (game.ActivePlayer.BattleZone.Contains(instance))
            return (game.ActivePlayer, "Your creature", game.ActivePlayer.BattleZone.IndexOf(instance));
        if (game.Opponent.BattleZone.Contains(instance))
            return (game.Opponent, "Their creature", game.Opponent.BattleZone.IndexOf(instance));
        if (game.ActivePlayer.ManaZone.Contains(instance))
            return (game.ActivePlayer, "Your mana card", game.ActivePlayer.ManaZone.IndexOf(instance));
        if (game.Opponent.ManaZone.Contains(instance))
            return (game.Opponent, "Their mana card", game.Opponent.ManaZone.IndexOf(instance));
        if (game.ActivePlayer.Graveyard.Contains(instance))
            return (game.ActivePlayer, "Your graveyard card", game.ActivePlayer.Graveyard.IndexOf(instance));
        return null;
    }

    // ------------------------------------------------------- shield trigger popup
    // An interrupt popup shown while the engine holds a shield-trigger window open
    // (an attacker just broke the defender's shields). The defender decides for each
    // pending card: play it for free, or leave it in hand. In AI duels the AI owns
    // the window for itself; only human-owned windows surface as a popup here.

    private void BuildShieldTriggerPopup()
    {
        _triggerPopup = new PanelContainer();
        _triggerPopup.AddThemeStyleboxOverride("panel", UiStyles.ModalCard());
        _triggerPopup.Visible = false;
        AddChild(_triggerPopup);

        _triggerPopupBox = new VBoxContainer();
        _triggerPopupBox.AddThemeConstantOverride("separation", 6);
        _triggerPopupBox.CustomMinimumSize = new Vector2(240, 0);
        _triggerPopup.AddChild(_triggerPopupBox);
    }

    private bool TriggerPopupNeedsDecision =>
        !_vsAi || ReferenceEquals(_game.ShieldTriggerOwner, _game.Player1);

    private void SyncShieldTriggerPopup()
    {
        if (_game is null || !_game.ShieldTriggerWindowActive || !TriggerPopupNeedsDecision)
        {
            HideTriggerPopup();
            return;
        }

        // The window interrupts the attacker's turn; while the human resolves it the
        // AI drive must pause (the engine rejects every action until it closes).
        _aiDriving = false;

        var fingerprint = string.Join("|", _game.PendingShieldTriggers
            .Select(x => x.Card.Id + "@" + _game!.ShieldTriggerOwner!.Hand.IndexOf(x)));
        ShowShieldTriggerPopup(fingerprint);
    }

    private void ShowShieldTriggerPopup(string fingerprint)
    {
        if (fingerprint == _triggerFingerprint && _triggerPopup.Visible)
            return;
        _triggerFingerprint = fingerprint;

        HideHandPopup();
        HideLookPopup();

        foreach (var child in _triggerPopupBox.GetChildren().OfType<Control>().ToList())
            child.QueueFree();

        var owner = _game.ShieldTriggerOwner;
        if (owner is null)
        {
            HideTriggerPopup();
            return;
        }
        var title = new Label
        {
            Text = $"Shield Trigger!\n{owner.Name}'s shields broke",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.CustomMinimumSize = new Vector2(240, 0);
        title.AddThemeFontSizeOverride("font_size", 14);
        title.AddThemeColorOverride("font_color", UiStyles.AccentText);
        _triggerPopupBox.AddChild(title);

        var hasPlayable = false;
        foreach (var instance in _game.PendingShieldTriggers.ToList())
        {
            var handIndex = owner.Hand.IndexOf(instance);
            if (handIndex < 0)
                continue;
            var card = instance.Card;
            if (card.IsEvolution)
            {
                // An evolution creature broken from the shields can only be evolved
                // onto a base creature, which a shield trigger can never do.
                var note = new Label
                {
                    Text = $"{card.Name} is an Evolution creature and stays in hand (it can only be evolved onto a creature).",
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                };
                note.AddThemeFontSizeOverride("font_size", 11);
                note.AddThemeColorOverride("font_color", UiStyles.MutedText);
                _triggerPopupBox.AddChild(note);
                continue;
            }
            var playable = !card.Effects.Any(e => e.NeedsTarget) || AnyLegalTriggerTarget(card);
            hasPlayable |= playable;
            var idx = handIndex;
            var btn = new Button { Text = $"Play {card.Name}", Disabled = !playable };
            btn.Pressed += () => PlayPendingTrigger(idx);
            _triggerPopupBox.AddChild(btn);
        }

        if (!hasPlayable)
        {
            var note = new Label
            {
                Text = "No effect can target anything right now - only \"leave in hand\" is available.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            note.AddThemeFontSizeOverride("font_size", 11);
            note.AddThemeColorOverride("font_color", UiStyles.MutedText);
            _triggerPopupBox.AddChild(note);
        }

        var leave = new Button { Text = "Leave them in hand" };
        leave.Pressed += () => DeclineShieldTriggers();
        _triggerPopupBox.AddChild(leave);

        _triggerPopup.Visible = true;
        CallDeferred(nameof(PositionTriggerPopup));
    }

    private void HideTriggerPopup()
    {
        _triggerPopup.Visible = false;
        if (_game is null || !_game.ShieldTriggerWindowActive)
            _triggerFingerprint = "";
    }

    private void PositionTriggerPopup()
    {
        if (_triggerPopup is null || !_triggerPopup.Visible)
            return;
        PositionPopupAtLeftSide(_triggerPopup);
    }

    private void PlayPendingTrigger(int handIndex)
    {
        var owner = _game!.ShieldTriggerOwner!;
        if (handIndex < 0 || handIndex >= owner.Hand.Count)
            return;
        var card = owner.Hand[handIndex].Card;
        if (!card.Effects.Any(e => e.NeedsTarget))
        {
            Safe(() => _game.PlayShieldTrigger(handIndex));
            return;
        }
        _mode = Mode.SelectShieldTarget;
        _triggerHandIndex = handIndex;
        HideTriggerPopup();
        Prompt($"Shield Trigger: choose a target for {card.Name}. Click a legal creature (either side), or press Esc to leave it in hand.");
        Refresh();
    }

    private void DeclineShieldTriggers() => Safe(() => _game.DeclineShieldTriggers());

    // ---------------------------------------------------------- scry tray popup
    // A "look at the top N cards of your deck, then put them back in any order" tap
    // ability (e.g. Garatyano) opens a short window in the engine. While it is open
    // the active player orders the exposed top-deck cards here: each row has the
    // face-up card plus move-up / move-down buttons, and Confirm submits the order.
    // Cancel (or Esc) puts the cards back in exactly the order they were drawn in.

    private void BuildScryPopup()
    {
        _scryPopup = new PanelContainer();
        _scryPopup.AddThemeStyleboxOverride("panel", UiStyles.ModalCard());
        _scryPopup.Visible = false;
        AddChild(_scryPopup);

        _scryPopupBox = new VBoxContainer();
        _scryPopupBox.AddThemeConstantOverride("separation", 6);
        _scryPopupBox.CustomMinimumSize = new Vector2(320, 0);
        _scryPopup.AddChild(_scryPopupBox);
    }

    private void SyncScryPopup()
    {
        if (_game is null || !_game.IsScryWindowActive)
        {
            HideScryPopup();
            return;
        }

        // The scry decision interrupts the active player's turn; the AI drive must
        // pause while the human reorders the deck (the engine rejects every other
        // action until the order is submitted).
        _aiDriving = false;

        var engineIds = string.Join("|", _game.ScryCards.Select(c => c.Id));
        if (engineIds != _scryFingerprint)
        {
            // A fresh window: adopt the engine's draw order as the starting order.
            _scryFingerprint = engineIds;
            _scryOrder.Clear();
            _scryOrder.AddRange(_game.ScryCards);
        }

        RebuildScryPopup();
    }

    private void RebuildScryPopup()
    {
        foreach (var child in _scryPopupBox.GetChildren().OfType<Control>().ToList())
            child.QueueFree();

        var owner = _game.ScryOwner;
        var title = new Label
        {
            Text = owner is null
                ? "Look at the top cards"
                : $"{owner.Name}: look at the top {_scryOrder.Count} card{(_scryOrder.Count == 1 ? "" : "s")},\nthen put them back in any order",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.CustomMinimumSize = new Vector2(320, 0);
        title.AddThemeFontSizeOverride("font_size", 14);
        title.AddThemeColorOverride("font_color", UiStyles.AccentText);
        _scryPopupBox.AddChild(title);

        if (_scryOrder.Count > 1)
        {
            var hint = new Label
            {
                Text = "Cards are listed top-down:\nuse the buttons to reorder, then Confirm.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            hint.AddThemeFontSizeOverride("font_size", 11);
            hint.AddThemeColorOverride("font_color", UiStyles.MutedText);
            _scryPopupBox.AddChild(hint);
        }

        for (var i = 0; i < _scryOrder.Count; i++)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);

            var view = new CardView(_scryOrder[i], ArtFor(_scryOrder[i]));
            view.SetCardSize(96, 134);
            view.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            row.AddChild(view);

            if (_scryOrder.Count > 1)
            {
                var idx = i;
                var up = new Button { Text = "Move up", Disabled = i == 0 };
                up.Pressed += () => SwapScryOrder(idx, idx - 1);
                row.AddChild(up);

                var down = new Button { Text = "Move down", Disabled = i == _scryOrder.Count - 1 };
                down.Pressed += () => SwapScryOrder(idx, idx + 1);
                row.AddChild(down);
            }

            _scryPopupBox.AddChild(row);
        }

        var confirm = new Button { Text = "Confirm order" };
        confirm.Pressed += SubmitScryOrder;
        _scryPopupBox.AddChild(confirm);

        // The engine's cancel semantics: put them back exactly as they were.
        var cancel = new Button { Text = "Cancel (keep draw order)" };
        cancel.Pressed += () => { _scryOrder.Clear(); _scryOrder.AddRange(_game.ScryCards); SubmitScryOrder(); };
        _scryPopupBox.AddChild(cancel);

        _scryPopup.Visible = true;
        CallDeferred(nameof(PositionScryPopup));
    }

    private void SwapScryOrder(int a, int b)
    {
        if (_game is null || !_game.IsScryWindowActive)
            return;
        if (a < 0 || b < 0 || a >= _scryOrder.Count || b >= _scryOrder.Count || a == b)
            return;
        (_scryOrder[a], _scryOrder[b]) = (_scryOrder[b], _scryOrder[a]);
        SyncScryPopup(); // rebuild with the updated order
    }

    private void SubmitScryOrder()
    {
        if (_game is null || !_game.IsScryWindowActive)
        {
            HideScryPopup();
            return;
        }
        Safe(() =>
        {
            _game.SubmitScryOrder(_scryOrder);
            HideScryPopup();
        });
        Refresh();
    }

    private void HideScryPopup()
    {
        _scryPopup.Visible = false;
        if (_game is null || !_game.IsScryWindowActive)
            _scryFingerprint = "";
    }

    private void PositionScryPopup()
    {
        if (_scryPopup is null || !_scryPopup.Visible)
            return;
        PositionPopupAtLeftSide(_scryPopup);
    }

    private bool AnyLegalTriggerTarget(Card spell)
    {
        if (_game is null || !_game.ShieldTriggerWindowActive)
            return false;
        var owner = _game.ShieldTriggerOwner;
        if (owner is null)
            return false;
        var foe = _game.Opponent;
        for (var i = 0; i < owner.BattleZone.Count; i++)
            if (_game.IsLegalSpellTarget(spell, owner, owner, i))
                return true;
        for (var i = 0; i < foe.BattleZone.Count; i++)
            if (_game.IsLegalSpellTarget(spell, owner, foe, i))
                return true;
        return false;
    }

    // --------------------------------------------------------- card inspector

    private void BuildInspectOverlay()
    {
        _inspectOverlay = new Control();
        _inspectOverlay.SetAnchorsPreset(LayoutPreset.FullRect);
        _inspectOverlay.Visible = false;
        AddChild(_inspectOverlay);

        var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.74f) };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        _inspectOverlay.AddChild(dim);

        var catchClicks = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        catchClicks.SetAnchorsPreset(LayoutPreset.FullRect);
        catchClicks.GuiInput += (@event) =>
        {
            if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
                CloseInspect();
        };
        _inspectOverlay.AddChild(catchClicks);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        center.MouseFilter = Control.MouseFilterEnum.Ignore;
        _inspectOverlay.AddChild(center);
        _inspectCenter = center;
    }

    private void ShowInspect(Card card)
    {
        if (_inspectView is not null)
        {
            _inspectView.QueueFree();
            _inspectView = null;
        }
        HideHandPopup();
        HideLookPopup();
        HideAttackMenu();
        // The "Look at card" view is just the raw card artwork: no frame, no text,
        // no numbers - big and clean so the player can read the card.
        var viewport = GetViewportRect().Size;
        var w = Mathf.Min(540f, viewport.X * 0.56f);
        var h = w * CardAspect;
        _inspectView = new Control
        {
            CustomMinimumSize = new Vector2(w, h),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _inspectView.Size = new Vector2(w, h);
        _inspectView.SetAnchorsPreset(LayoutPreset.TopLeft);

        var artPath = ArtFor(card);
        var tex = artPath is not null && ResourceLoader.Exists(artPath)
            ? ResourceLoader.Load<Texture2D>(artPath)
            : null;
        var art = new TextureRect
        {
            Texture = tex,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        art.SetAnchorsPreset(LayoutPreset.FullRect);
        _inspectView.AddChild(art);
        _inspectCenter.AddChild(_inspectView);
        _inspectOverlay.Visible = true;
    }

    private void CloseInspect()
    {
        _inspectOverlay.Visible = false;
        if (_inspectView is not null)
        {
            _inspectView.QueueFree();
            _inspectView = null;
        }
    }

    private void HideInspectIfOpen()
    {
        if (_inspectOverlay.Visible)
            CloseInspect();
    }

    // -------------------------------------------------------- graveyard viewer

    private void BuildGraveyardOverlay()
    {
        _graveOverlay = new Control();
        _graveOverlay.SetAnchorsPreset(LayoutPreset.FullRect);
        _graveOverlay.Visible = false;
        AddChild(_graveOverlay);

        var dim = new ColorRect { Color = new Color(0f, 0f, 0f, 0.82f) };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        _graveOverlay.AddChild(dim);

        var catchClicks = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        catchClicks.SetAnchorsPreset(LayoutPreset.FullRect);
        catchClicks.GuiInput += (@event) =>
        {
            if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
                CloseGraveyard();
        };
        _graveOverlay.AddChild(catchClicks);

        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        _graveOverlay.AddChild(center);

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiStyles.ModalCard());
        center.AddChild(panel);

        var box = new VBoxContainer { CustomMinimumSize = new Vector2(720, 0) };
        box.AddThemeConstantOverride("separation", 10);
        panel.AddChild(box);

        _graveTitle = new Label
        {
            Text = "GRAVE", 
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _graveTitle.AddThemeFontSizeOverride("font_size", 22);
        _graveTitle.AddThemeColorOverride("font_color", UiStyles.TitleText);
        box.AddChild(_graveTitle);

        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(680, 420),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        box.AddChild(scroll);

        _graveList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _graveList.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(_graveList);

        var close = new Button { Text = "Close" };
        close.Pressed += CloseGraveyard;
        box.AddChild(close);
    }

    private void ShowGraveyard(Player p)
    {
        foreach (var child in _graveList.GetChildren().ToList())
        {
            _graveList.RemoveChild(child);
            child.QueueFree();
        }

        _graveTitle.Text = $"GRAVE  ({p.Graveyard.Count})";

        // Newest first (the top of the pile), matching the visible pile face.
        for (var i = p.Graveyard.Count - 1; i >= 0; i--)
        {
            var inst = p.Graveyard[i];
            var card = inst.Card;
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            _graveList.AddChild(row);

            var artPath = ArtFor(card);
            var view = new CardView(card, artPath, faceDown: false, artOnly: true);
            var w = _cardW * 0.6f;
            var h = _cardH * 0.6f;
            view.SetCardSize(w, h);
            view.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            row.AddChild(view);

            var info = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
            info.AddThemeConstantOverride("separation", 2);

            var name = new Label
            {
                Text = card.Name,
                HorizontalAlignment = HorizontalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            name.AddThemeFontSizeOverride("font_size", 14);
            name.AddThemeColorOverride("font_color", CivilizationPalette.Color(card.Civilization).Lightened(0.25f));
            info.AddChild(name);

            var text = new Label
            {
                Text = DescribeCard(card),
                HorizontalAlignment = HorizontalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            text.AddThemeFontSizeOverride("font_size", 11);
            text.AddThemeColorOverride("font_color", UiStyles.BodyText);
            info.AddChild(text);

            var look = new Button { Text = "Look at card", CustomMinimumSize = new Vector2(0, 24) };
            look.Pressed += () => ShowInspect(card);
            info.AddChild(look);

            row.AddChild(info);
        }

        _graveOverlay.Visible = true;
    }

    private void CloseGraveyard()
    {
        _graveOverlay.Visible = false;
        HideInspectIfOpen();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event.IsActionPressed("ui_cancel"))
        {
            if (_graveOverlay.Visible)
            {
                CloseGraveyard();
                GetViewport().SetInputAsHandled();
            }
            else if (_inspectOverlay.Visible)
            {
                CloseInspect();
                GetViewport().SetInputAsHandled();
            }
            else if (_attackMenu.Visible)
            {
                // Close the attacker's menu but keep the selection; a second Esc cancels.
                HideAttackMenu();
                GetViewport().SetInputAsHandled();
            }
            else if (_tapMenu.Visible)
            {
                HideTapTargetMenu();
                ResetInteraction();
                Prompt("Tap ability cancelled - pick a creature to attack when ready.");
                Refresh();
                GetViewport().SetInputAsHandled();
            }
            else if (_mode == Mode.SelectBlock && _attackerIndex >= 0)
            {
                // The defender declines to block: the pending attack hits the shields.
                Safe(() =>
                {
                    PlayAttackFx(_attackerIndex);
                    _game.AttackPlayer(_attackerIndex);
                });
                GetViewport().SetInputAsHandled();
            }
            else if (_mode == Mode.SelectTarget)
            {
                ResetInteraction();
                Prompt("Attack cancelled - pick a creature to attack when ready.");
                Refresh();
                GetViewport().SetInputAsHandled();
            }
            else if (_mode is Mode.SelectSpellTarget or Mode.SelectShieldTarget or Mode.SelectSummonTarget or Mode.SelectEvolveTarget or Mode.EvolveBase)
            {
                ResetInteraction();
                Refresh();
                GetViewport().SetInputAsHandled();
            }
            else if (_game is not null && _game.IsScryWindowActive)
            {
                // Esc cancels a pending deck-order decision: the engine puts the
                // looked-at cards back in exactly the order they were drawn in.
                Safe(() => _game.SubmitScryOrder(_game.ScryCards.ToList()));
                ResetInteraction();
                Refresh();
                GetViewport().SetInputAsHandled();
            }
            else if (_mode == Mode.SelectShieldPeek)
            {
                ResetInteraction();
                Prompt("Shield peek cancelled.");
                Refresh();
                GetViewport().SetInputAsHandled();
            }
            else if (_handPopup.Visible || _lookPopup.Visible)
            {
                ResetInteraction();
                Refresh();
                GetViewport().SetInputAsHandled();
            }
            else if (_awaitingBlockChoice)
            {
                // Esc also declines to block - the AI attack resolves against the shields.
                TakeHit();
                GetViewport().SetInputAsHandled();
            }
        }
    }

    /// <summary>
    /// The human defender takes the AI's incoming attack without blocking: the attack
    /// resolves against the shields and the AI resumes driving its turn.
    /// </summary>
    private void TakeHit()
    {
        if (_game is null || !_awaitingBlockChoice)
            return;
        Safe(() =>
        {
            PlayAttackFx(_pendingAiAttackerIndex);
            _game.AttackPlayer(_pendingAiAttackerIndex);
            _awaitingBlockChoice = false;
            ResumeAi();
        });
    }

    private static string DescribeCard(Card card)
    {
        var lines = new List<string>
        {
            card.IsEvolution
                ? $"Evolution Creature  -  {card.Power} power"
                : card.IsCreature
                    ? $"Creature  -  {card.ManaCost} mana  -  {card.Power} power"
                    : $"Spell  -  {card.ManaCost} mana",
            $"Civilization: {card.Civilization}",
        };
        if (!string.IsNullOrEmpty(card.Race))
            lines.Add($"Race: {card.Race}");
        if (card.IsEvolution)
            lines.Add($"EVOLUTION - no mana. Put on top of one of your {card.EvolutionOf} creatures.");
        var keywords = new List<string>();
        if (card.HasKeyword(Keyword.Blocker)) keywords.Add("Blocker");
        if (card.HasKeyword(Keyword.ShieldTrigger)) keywords.Add("Shield trigger");
        if (card.HasKeyword(Keyword.SpeedAttacker)) keywords.Add("Speed attacker");
        if (card.HasKeyword(Keyword.Slayer)) keywords.Add("Slayer");
        if (card.HasKeyword(Keyword.PowerAttacker)) keywords.Add("Power attacker");
        if (card.HasKeyword(Keyword.DoubleBreaker)) keywords.Add("Double breaker");
        if (card.HasKeyword(Keyword.TripleBreaker)) keywords.Add("Triple breaker");
        if (card.HasKeyword(Keyword.Unblockable)) keywords.Add("Unblockable");
        if (card.HasKeyword(Keyword.CannotAttackPlayers)) keywords.Add("Can't attack players");
        if (card.HasKeyword(Keyword.CannotAttackCreatures)) keywords.Add("Can't attack creatures");
        if (card.HasKeyword(Keyword.CanAttackUntappedCreatures)) keywords.Add("Can attack untapped creatures");
        if (card.HasKeyword(Keyword.CannotBeAttacked)) keywords.Add("Can't be attacked");
        if (card.HasKeyword(Keyword.AttacksEachTurn)) keywords.Add("Attacks each turn");
        if (card.HasKeyword(Keyword.Charger)) keywords.Add("Charger");
        if (card.HasKeyword(Keyword.Survivor)) keywords.Add("Survivor");
        if (card.HasKeyword(Keyword.Stealth)) keywords.Add("Stealth");
        if (card.HasKeyword(Keyword.SummonRequiresSpellCast)) keywords.Add("Summon only if you cast a spell this turn");
        if (card.HasKeyword(Keyword.CannotAttackOutnumbered)) keywords.Add("Can't attack while outnumbered");
        if (keywords.Count > 0)
            lines.Add(string.Join("  ·  ", keywords));

        lines.AddRange(card.Effects.Select(EffectText).Where(t => t.Length > 0));
        return string.Join("\n", lines);
    }

    private static string EffectText(CardEffect effect) => effect.Id switch
    {
        EffectId.OnPlay_Draw => effect.Value > 1
            ? $"When you put this creature into the battle zone, draw {effect.Value} cards."
            : "When you put this creature into the battle zone, draw a card.",
        EffectId.OnDestroyed_Draw => effect.Value > 1
            ? $"When this creature is destroyed, draw {effect.Value} cards."
            : "When this creature is destroyed, draw a card.",
        EffectId.Spell_DestroyPowerAtMost => $"Destroy one of your opponent's creatures that has power {effect.Value} or less.",
        EffectId.Spell_ReturnToHand => "Return one of your opponent's creatures to its owner's hand.",
        EffectId.Spell_TapCreature => "Tap one of your opponent's creatures.",
        EffectId.Spell_UntapOwnCreature => "Untap one of your creatures.",
        EffectId.Spell_Draw => effect.Value > 1 ? $"Draw {effect.Value} cards." : "Draw a card.",
        EffectId.Spell_BoostPower => $"Until the end of the turn, one of your creatures gets +{effect.Value} power.",
        EffectId.PowerAttacker_AttackBoost => $"Power attacker +{effect.Value} (while attacking, this creature has +{effect.Value} power).",

        EffectId.OnPlay_TapCreature => "When you put this creature into the battle zone, you may tap one creature.",
        EffectId.OnPlay_ReturnToHand => "When you put this creature into the battle zone, you may return one creature to its owner's hand.",
        EffectId.OnPlay_DestroyPowerAtMost => $"When you put this creature into the battle zone, you may destroy one creature that has power {effect.Value} or less.",
        EffectId.OnPlay_UntapOwnCreature => "When you put this creature into the battle zone, you may untap one of your creatures.",
        EffectId.OnPlay_UntapAllOwnCreatures => "When you put this creature into the battle zone, untap each of your creatures.",
        EffectId.OnPlay_ChargeMana => "When you put this creature into the battle zone, put the top card of your deck into your mana zone.",
        EffectId.OnDestroyed_ToHand => "If this creature would be destroyed, put it into its owner's hand instead.",
        EffectId.OnDestroyed_ToMana => "If this creature would be destroyed, put it into its owner's mana zone instead.",
        EffectId.Spell_DestroyAllCreatures => "Destroy all creatures in the battle zone.",
        EffectId.Spell_ReturnUpToToHand => $"Return up to {effect.Value} creatures in the battle zone to their owners' hands.",
        EffectId.Spell_ChargeMana => "Put the top card of your deck into your mana zone.",
        EffectId.Spell_DiscardRandom => $"Your opponent discards {effect.Value} random cards from hand.",
        EffectId.StaticPower_AttackPerGraveyardCiv => $"While attacking, this creature gets +{effect.Value} power for each {effect.Data} card in your graveyard.",
        EffectId.StaticPower_AttackPerOtherCreature => $"While attacking, this creature gets +{effect.Value} power for each other creature you have.",
        EffectId.StaticPower_AttackWhileHaveRace => $"While attacking, this creature gets +{effect.Value} power while you have a {effect.Data} in the battle zone.",
        EffectId.StaticPower_AlwaysWhileHaveRace => $"This creature gets +{effect.Value} power while you have a {effect.Data} in the battle zone.",
        EffectId.StaticPower_AlwaysPerOtherCreature => string.IsNullOrWhiteSpace(effect.Data)
            ? $"This creature gets +{effect.Value} power for each other creature you have."
            : $"This creature gets +{effect.Value} power for each other {effect.Data} creature you have.",
        EffectId.StaticPower_AuraRace => $"Each other {effect.Data} creature in the battle zone gets +{effect.Value} power.",
        EffectId.CostIncrease_Summon_ByCiv => $"Each {effect.Data} creature costs {effect.Value} more to summon.",
        EffectId.CostIncrease_Cast_ByCiv => $"Each {effect.Data} spell costs {effect.Value} more to cast.",
        EffectId.CostDecrease_Summon_All => $"Your creatures cost {effect.Value} less to summon.",
        EffectId.CostDecrease_Cast_All => $"Your spells cost {effect.Value} less to cast.",
        EffectId.CostDecrease_Summon_ByRace => $"Your {effect.Data} creatures cost {effect.Value} less to summon.",
        _ => "",
    };

    private VBoxContainer BuildZone(Civilization tint, string caption, out Label titleLabel)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        box.SizeFlagsVertical = SizeFlags.ExpandFill;

        var title = new Label { Text = caption };
        title.AddThemeFontSizeOverride("font_size", 13);
        title.AddThemeColorOverride("font_color", tint == Civilization.Zero ? UiStyles.BodyText : CivilizationPalette.Color(tint).Lightened(0.35f));
        box.AddChild(title);
        titleLabel = title;

        var flow = new HBoxContainer();
        flow.Alignment = BoxContainer.AlignmentMode.Center;
        flow.AddThemeConstantOverride("separation", 8);
        // Hug content so the CenterContainer below can center the whole row; the zone
        // box keeps its stretch via the wrapper.
        flow.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        flow.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        box.AddChild(CenteredFlowHost(flow));
        return box;
    }

    /// <summary>
    /// Wraps a zone's cards in a mouse-transparent CenterContainer so the group of
    /// cards is centered horizontally AND vertically inside its zone strip.
    /// </summary>
    private static CenterContainer CenteredFlowHost(HBoxContainer flow)
    {
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        center.SizeFlagsVertical = SizeFlags.ExpandFill;
        center.AddChild(flow);
        return center;
    }

    private static Label StackLabel(string text) => new()
    {
        Text = text,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        CustomMinimumSize = new Vector2(0, 26),
    };

    /// <summary>
    /// Builds a compact DECK or GRAVE pile: a mini card showing the artwork
    /// CENTERED and UNCROPPED (keep-contained) on a solid backing, with a count
    /// caption underneath. The deck always shows the card back; the graveyard shows
    /// its top card's art (falling back to the back when empty).
    /// </summary>
    private void BuildPile(HBoxContainer host, bool isTop, bool isDeck, out VBoxContainer pile, out Label caption)
    {
        pile = new VBoxContainer();
        pile.Alignment = BoxContainer.AlignmentMode.Center;
        pile.AddThemeConstantOverride("separation", 2);
        pile.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        pile.CustomMinimumSize = new Vector2(_stackW + 14f, 0);

        caption = new Label
        {
            Text = isDeck ? "DECK  0" : "GRAVE  0",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        caption.AddThemeFontSizeOverride("font_size", 11);
        caption.AddThemeColorOverride("font_color", UiStyles.MutedText);
        pile.AddChild(caption);

        _ = isTop;
        host.AddChild(pile);
    }

    /// <summary>Rebuilds a deck/grave pile's mini face + count inside its stored VBox.</summary>
    private void UpdatePile(VBoxContainer pile, Label caption, bool faceUp, Card? topCard, string label, int count, Action? onFaceClick = null)
    {
        // The pile VBox holds [caption, face]; rebuild the face each refresh so the top
        // card / count always reflects the live state.
        foreach (var child in pile.GetChildren().OfType<Control>().Where(c => c is not Label).ToList())
        {
            pile.RemoveChild(child);
            child.QueueFree();
        }

        var face = new Panel { MouseFilter = onFaceClick is null ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Stop };
        face.CustomMinimumSize = new Vector2(_stackW, _stackH);
        face.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        face.AddThemeStyleboxOverride("panel", PileFaceStyle());
        if (onFaceClick is not null)
            face.GuiInput += (@event) =>
            {
                if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
                    onFaceClick();
            };

        var artPath = faceUp && topCard is not null ? ArtFor(topCard) : null;
        var tex = (artPath is not null && ResourceLoader.Exists(artPath))
            ? ResourceLoader.Load<Texture2D>(artPath)
            : ResourceLoader.Load<Texture2D>("res://assets/art/cards/BackCard.webp");
        if (tex is not null)
        {
            var img = new TextureRect
            {
                Texture = tex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            img.SetAnchorsPreset(LayoutPreset.FullRect);
            face.AddChild(img);
        }
        else
        {
            var mark = new Label
            {
                Text = "DM",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            mark.AddThemeFontSizeOverride("font_size", 22);
            mark.AddThemeColorOverride("font_color", new Color(0.45f, 0.52f, 0.68f));
            face.AddChild(mark);
        }

        pile.AddChild(face);
        if (onFaceClick is not null)
        {
            // The whole pile area (caption + margins included) opens the viewer.
            pile.MouseFilter = Control.MouseFilterEnum.Stop;
            pile.GuiInput += (@event) =>
            {
                if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
                    onFaceClick();
            };
        }
        caption.Text = $"{label}  {count}";
    }

    private static StyleBoxFlat PileFaceStyle()
    {
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.07f, 0.11f, 1f),
            BorderColor = new Color(0.22f, 0.26f, 0.38f, 1f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
        };
        sb.SetBorderWidthAll(2);
        return sb;
    }

    private static HBoxContainer GetFlow(VBoxContainer box)
    {
        var flow = box.FindChildren("*", "HBoxContainer", recursive: true, owned: false)
            .OfType<HBoxContainer>()
            .FirstOrDefault();
        if (flow is not null)
            return flow;
        var created = new HBoxContainer();
        created.AddThemeConstantOverride("separation", 8);
        box.AddChild(CenteredFlowHost(created));
        return created;
    }

    private static StyleBoxFlat TableBackdrop()
    {
        var sb = new StyleBoxFlat { BgColor = new Color(0.02f, 0.03f, 0.05f, 1f) };
        return sb;
    }

    private static StyleBoxFlat HudPanel()
    {
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.07f, 0.10f, 0.96f),
            BorderColor = new Color(0.28f, 0.34f, 0.45f, 1f),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
        };
        sb.SetBorderWidthAll(1);
        return sb;
    }

    // ---------------------------------------------------------- deck selection

    private void BuildDeckSelection()
    {
        _selectRoot = new Control();
        _selectRoot.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_selectRoot);

        var backdrop = new Panel();
        backdrop.SetAnchorsPreset(LayoutPreset.FullRect);
        backdrop.AddThemeStyleboxOverride("panel", UiStyles.ModalBackdrop());
        _selectRoot.AddChild(backdrop);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        _selectRoot.AddChild(center);

        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel", UiStyles.ModalCard());
        center.AddChild(card);

        var box = new VBoxContainer();
        box.CustomMinimumSize = new Vector2(760, 0);
        box.AddThemeConstantOverride("separation", 14);
        card.AddChild(box);

        var title = new Label { Text = "Choose Your Duel", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 28);
        title.AddThemeColorOverride("font_color", UiStyles.TitleText);
        box.AddChild(title);

        var subtitle = new Label
        {
            Text = "Select a starter deck for yourself and one for the opponent.",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        subtitle.AddThemeColorOverride("font_color", UiStyles.BodyText);
        box.AddChild(subtitle);

        box.AddChild(new HSeparator());

        // Your deck.
        _myDeckDesc = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(0, 44) };
        _myDeckDesc.AddThemeColorOverride("font_color", UiStyles.MutedText);
        box.AddChild(AddPickerRow("Your Deck", out _myDeckPick, out _, _myDeckDesc));

        // Opponent row: deck + kind.
        _oppDeckDesc = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(0, 44) };
        _oppDeckDesc.AddThemeColorOverride("font_color", UiStyles.MutedText);
        box.AddChild(AddPickerRow("Opponent Deck", out _oppDeckPick, out _oppDeckCaption, _oppDeckDesc));

        var kindRow = new HBoxContainer();
        kindRow.Alignment = BoxContainer.AlignmentMode.Center;
        kindRow.AddThemeConstantOverride("separation", 10);
        box.AddChild(kindRow);

        var kindLabel = new Label { Text = "Opponent:" };
        kindLabel.AddThemeColorOverride("font_color", UiStyles.BodyText);
        kindRow.AddChild(kindLabel);

        _oppKindPick = new OptionButton { CustomMinimumSize = new Vector2(240, 0) };
        _oppKindPick.AddItem("AI Opponent", 0);
        _oppKindPick.AddItem("Human (hotseat)", 1);
        _oppKindPick.Select(0);
        kindRow.AddChild(_oppKindPick);

        box.AddChild(new HSeparator());

        _selectStatus = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(0, 24) };
        _selectStatus.AddThemeColorOverride("font_color", UiStyles.BodyText);
        box.AddChild(_selectStatus);

        var buttons = new HBoxContainer();
        buttons.Alignment = BoxContainer.AlignmentMode.Center;
        buttons.AddThemeConstantOverride("separation", 12);
        box.AddChild(buttons);

        var start = new Button { Text = "Start Duel" };
        start.Pressed += OnStartDuel;
        buttons.AddChild(start);

        var back = new Button { Text = "Main Menu" };
        back.Pressed += OnBackToMenu;
        buttons.AddChild(back);

        try
        {
            _starterDecks.Clear();
            _starterDecks.AddRange(StarterDecks.LoadAll());
            foreach (var d in _starterDecks)
            {
                _myDeckPick.AddItem($"{d.Name}  ({d.Archetype})");
                _oppDeckPick.AddItem($"{d.Name}  ({d.Archetype})");
            }
            start.Disabled = _starterDecks.Count == 0;
            if (_starterDecks.Count > 0)
            {
                _myDeckPick.Select(0);
                _oppDeckPick.Select(1 % _starterDecks.Count);
                _myDeckPick.ItemSelected += _ => UpdateDeckDescs();
                _oppDeckPick.ItemSelected += _ => UpdateDeckDescs();
                _oppKindPick.ItemSelected += _ =>
                {
                    var isAi = _oppKindPick.GetSelectedId() == 0;
                    _oppDeckCaption.Text = isAi ? "AI Deck" : "Player 2 Deck";
                };
                UpdateDeckDescs();
            }
            else
            {
                _selectStatus.Text = "No starter decks registered - add some in the Deck Builder.";
                _selectStatus.Modulate = UiStyles.ErrorText;
            }
        }
        catch (Exception ex)
        {
            _selectStatus.Text = $"Could not load starter decks: {ex.Message}";
            _selectStatus.Modulate = UiStyles.ErrorText;
        }
    }

    private VBoxContainer AddPickerRow(string caption, out OptionButton pick, out Label captionLabel, Label desc)
    {
        var wrap = new VBoxContainer();
        wrap.AddThemeConstantOverride("separation", 4);
        wrap.Alignment = BoxContainer.AlignmentMode.Center;

        var row = new HBoxContainer();
        row.Alignment = BoxContainer.AlignmentMode.Center;
        row.AddThemeConstantOverride("separation", 10);
        wrap.AddChild(row);

        var label = new Label { Text = caption };
        label.CustomMinimumSize = new Vector2(150, 0);
        label.AddThemeColorOverride("font_color", UiStyles.BodyText);
        row.AddChild(label);
        captionLabel = label;

        pick = new OptionButton { CustomMinimumSize = new Vector2(360, 0) };
        row.AddChild(pick);

        wrap.AddChild(desc);
        return wrap;
    }

    private void UpdateDeckDescs()
    {
        _myDeckDesc.Text = DescFor(_myDeckPick);
        _oppDeckDesc.Text = DescFor(_oppDeckPick);
    }

    private string DescFor(OptionButton pick)
    {
        var deck = _starterDecks.FirstOrDefault(d => d.Id == SelectedDeckId(pick));
        return deck is null ? "" : $"  {deck.Archetype}: {deck.Tagline}";
    }

    // ---------------------------------------------------------- interaction flow

    private bool CanAct =>
        _game is not null && !_game.IsGameOver && _game.Phase == GamePhase.Main && !_aiDriving && !_awaitingBlockChoice;

    /// <summary>True when the card row that was clicked belongs to the active player.</summary>
    private bool SideIsActive(bool isBottomSide) =>
        (isBottomSide && ReferenceEquals(_game!.ActivePlayer, _game.Player1))
     || (!isBottomSide && ReferenceEquals(_game.ActivePlayer, _game.Player2));

    private void OnEndTurn()
    {
        if (_game is null || _game.IsGameOver)
            return;
        CaptureFx();
        try
        {
            _game.EndMainPhase();
            _game.EndTurn();
            Sfx.Play(SfxId.Turn);
            if (!_game.IsGameOver)
            {
                _game.StartTurn();
                _game.Draw();
            }
        }
        catch (RuleViolationException ex)
        {
            Notice(ex.Message);
        }
        ResetInteraction();

        if (!_game.IsGameOver && _vsAi && ReferenceEquals(_game.ActivePlayer, _ai?.Self))
        {
            _aiDriving = true;
            _aiTimer = AiStepDelay;
        }
        Refresh();
    }

    private void OnBackToMenu() => GetTree().ChangeSceneToFile(MainMenuPath);

    private void OnHandClicked(bool isBottomSide, int index)
    {
        if (!CanAct || !SideIsActive(isBottomSide))
            return;
        if (_mode == Mode.SelectBlock)
            return;
        if (_game!.ShieldTriggerWindowActive)
        {
            // Trigger interrupts resolve through the popup; hand clicks only inspect.
            ShowLookPopup(_game.ActivePlayer.Hand[index].Card);
            return;
        }
        if (_mode is Mode.SelectSpellTarget or Mode.SelectShieldTarget or Mode.SelectSummonTarget or Mode.SelectEvolveTarget or Mode.EvolveBase)
        {
            var cancelled = _mode is Mode.SelectSpellTarget or Mode.SelectSummonTarget or Mode.SelectEvolveTarget or Mode.EvolveBase
                && _selectedHandSide == isBottomSide && _selectedHandIndex == index;
            ResetInteraction();
            if (cancelled)
            {
                Refresh();
                return;
            }
        }

        // Clicking the already-selected card again deselects it (and dismisses the popup).
        if (_mode == Mode.SelectHand && _selectedHandSide == isBottomSide && _selectedHandIndex == index)
        {
            ResetInteraction();
            Refresh();
            return;
        }

        ResetInteraction();
        _mode = Mode.SelectHand;
        _selectedHandSide = isBottomSide;
        _selectedHandIndex = index;
        Refresh();
        ShowHandPopup(isBottomSide, index);
    }

    private void DoCharge(int index)
    {
        Safe(() =>
        {
            _game.PlayManaToManaZone(index);
            Sfx.Play(SfxId.Mana);
        });
    }

    private void DoSummon(int index)
    {
        Safe(() =>
        {
            var summoned = _game.SummonCreature(index);
            PlaySummonFx(summoned.Card);
            if (_game.IsGameOver || _game.Winner is not null)
                Notice($"{summoned.Card.Name} summoned.");
        });
    }

    private void DoSummonTargeted(int index)
    {
        var creature = _game!.ActivePlayer.Hand[index].Card;
        _selectedHandSide = _game.ActivePlayer == _game.Player1;
        _selectedHandIndex = index;
        _summonHandIndex = index;
        _mode = Mode.SelectSummonTarget;
        HideHandPopup();
        Prompt($"Choose an on-play target for {creature.Name}: click a legal creature (either side), or click the card again to cancel the summon.");
        Refresh();
    }

    private void DoCast(int index)
    {
        var spell = _game!.ActivePlayer.Hand[index].Card;
        if (spell.Effects.Any(e => e.NeedsTarget))
        {
            _selectedHandSide = _game.ActivePlayer == _game.Player1;
            _selectedHandIndex = index;
            _spellHandIndex = index;
            var multi = spell.Effects.FirstOrDefault(e => e.Id == EffectId.Spell_ReturnUpToToHand);
            if (multi is { Value: > 1 })
            {
                _maxTargets = multi.Value;
                _spellTargetPicks.Clear();
                _mode = Mode.SelectSpellTarget;
                HideHandPopup();
                Prompt($"Choose up to {multi.Value} creatures for {spell.Name}: click legal creatures to select (click again to unselect), then confirm the cast.");
                ShowSpellTargetConfirm(spell);
                Refresh();
                return;
            }
            _mode = Mode.SelectSpellTarget;
            HideHandPopup();
            Prompt($"Choose a target for {spell.Name}: click a legal creature (either side), or click the card again to cancel.");
            Refresh();
            return;
        }
        Safe(() =>
        {
            _game.CastSpell(index);
            PlayCastFx(spell);
        });
    }

    private void DoEvolve(int index)
    {
        var creature = _game!.ActivePlayer.Hand[index].Card;
        if (DuelGame.HasOnPlayTargetChoice(creature) && AnyLegalOnPlayTarget(creature))
        {
            _selectedHandSide = _game.ActivePlayer == _game.Player1;
            _selectedHandIndex = index;
            _evolveHandIndex = index;
            _mode = Mode.SelectEvolveTarget;
            HideHandPopup();
            Prompt($"Choose an on-play target for {creature.Name}: click a legal creature (either side), or click the card again to cancel.");
            Refresh();
            return;
        }
        StartEvolveBaseSelection(index);
    }

    private void DoEvolveTargeted(int index)
    {
        var creature = _game!.ActivePlayer.Hand[index].Card;
        _selectedHandSide = _game.ActivePlayer == _game.Player1;
        _selectedHandIndex = index;
        _evolveHandIndex = index;
        _mode = Mode.SelectEvolveTarget;
        HideHandPopup();
        Prompt($"Choose an on-play target for {creature.Name}: click a legal creature (either side), or click the card again to cancel.");
        Refresh();
    }

    private void StartEvolveBaseSelection(int index)
    {
        var creature = _game!.ActivePlayer.Hand[index].Card;
        HideHandPopup();
        _evolveHandIndex = index;
        _mode = Mode.EvolveBase;
        Prompt($"Evolve {creature.Name} onto a {creature.EvolutionOf} creature: click one of your matching-race creatures, or click the hand card again to cancel.");
        Refresh();
    }

    private void OnBattleClicked(bool isBottomSide, int index)
    {
        if (_game is null || _game.IsGameOver)
            return;
        if (_awaitingBlockChoice)
        {
            // The AI swung for shields; the human defender (non-active side) clicks a blocker here.
            if (!SideIsActive(isBottomSide) && IsDefenderBlocker(index))
            {
                Safe(() =>
                {
                    PlayAttackFx(_pendingAiAttackerIndex, BattleInstanceCenter(_game.Opponent.BattleZone[index]));
                    _game.AttackPlayer(_pendingAiAttackerIndex, _game.Opponent, index);
                    _awaitingBlockChoice = false;
                    ResumeAi();
                });
                ResetInteraction();
                Refresh();
            }
            return;
        }
        if (!CanAct)
        {
            // No action applies right now (opponent's turn, etc.) - offer inspection.
            if (BoardCardAt(isBottomSide, index) is { } idleCard)
                ShowLookPopup(idleCard);
            return;
        }

        switch (_mode)
        {
            case Mode.Idle:
            case Mode.SelectHand:
                if (SideIsActive(isBottomSide))
                {
                    var candidate = _game.ActivePlayer.BattleZone[index];
                    if (candidate.IsTapped || candidate.IsSummoningSick)
                    {
                        ShowLookPopup(candidate.Card);
                        Prompt(candidate.IsTapped
                            ? $"{candidate.Card.Name} has already attacked this turn (tapped). Pick an untapped creature."
                            : $"{candidate.Card.Name} just entered play and can't attack until your next turn. Pick an untapped creature.");
                        return;
                    }
                    _attackerIndex = index;
                    _mode = Mode.SelectTarget;
                    HideHandPopup();
                    HideLookPopup();
                    HideAttackMenu();
                    ShowAttackMenu(_game.ActivePlayer.BattleZone[index].Card);
                    var finale = _game.Opponent.ShieldCount == 0
                        ? " The enemy has NO shields left: click the enemy shields zone to land the final attack and win!"
                        : "";
                    Prompt($"{_game.ActivePlayer.BattleZone[index].Card.Name} is attacking! Use the menu to look at it or attack. Choose a target: a tapped enemy creature, or the enemy shields. Click the enemy zone to attack.{finale}");
                }
                else
                {
                    // The enemy's field card has no action available - just let the player look.
                    if (BoardCardAt(isBottomSide, index) is { } enemyCard)
                        ShowLookPopup(enemyCard);
                }
                break;

            case Mode.SelectTarget:
                if (SideIsActive(isBottomSide))
                {
                    if (index == _attackerIndex)
                    {
                        // Re-clicking the selected attacker re-opens (or dismisses) its menu.
                        if (_attackMenu.Visible)
                            HideAttackMenu();
                        else
                            ShowAttackMenu(_game.ActivePlayer.BattleZone[index].Card);
                        break;
                    }
                    var candidate = _game.ActivePlayer.BattleZone[index];
                    if (!candidate.IsTapped && !candidate.IsSummoningSick)
                    {
                        _attackerIndex = index;
                        ShowAttackMenu(candidate.Card);
                        var finale = _game.Opponent.ShieldCount == 0
                            ? " (The enemy has no shields - the next direct hit wins!)"
                            : "";
                        Prompt($"Pick a target for the new attacker ({_game.ActivePlayer.BattleZone[index].Card.Name}).{finale}");
                    }
                    else
                    {
                        Prompt(candidate.IsTapped
                            ? $"{candidate.Card.Name} has already attacked this turn (tapped)."
                            : $"{candidate.Card.Name} just entered play and can't attack until your next turn.");
                    }
                    break;
                }
                var target = _game.Opponent.BattleZone[index];
                if (target.IsTapped)
                {
                    Safe(() =>
                    {
                        PlayAttackFx(_attackerIndex, BattleInstanceCenter(target));
                        _game.AttackCreature(_attackerIndex, index);
                    });
                    ResetInteraction();
                }
                else
                {
                    ShowLookPopup(target.Card);
                }
                break;

            case Mode.SelectSpellTarget when _spellHandIndex >= 0:
            {
                var targetOwner = isBottomSide ? _game.Player1 : _game.Player2;
                var spell = _game.ActivePlayer.Hand[_spellHandIndex].Card;
                if (index >= 0 && index < targetOwner.BattleZone.Count
                    && _game.IsLegalSpellTarget(spell, _game.ActivePlayer, targetOwner, index))
                {
                    if (_maxTargets > 1)
                    {
                        var pick = _spellTargetPicks.Find(p => ReferenceEquals(p.Owner, targetOwner) && p.Index == index);
                        if (pick.Owner is not null)
                            _spellTargetPicks.Remove(pick);
                        else if (_spellTargetPicks.Count < _maxTargets)
                            _spellTargetPicks.Add(new SpellTarget(targetOwner, index));
                        UpdateSpellTargetConfirm(spell);
                        Refresh();
                    }
                    else
                    {
                        var hand = _spellHandIndex;
                        Safe(() =>
                        {
                            var spell = _game.ActivePlayer.Hand[hand].Card;
                            _game.CastSpell(hand, targetOwner, index);
                            PlayCastFx(spell);
                        });
                    }
                }
                else if (BoardCardAt(isBottomSide, index) is { } spellLook)
                {
                    ShowLookPopup(spellLook);
                }
                break;
            }

            case Mode.SelectSummonTarget when _summonHandIndex >= 0:
            {
                var summonOwner = isBottomSide ? _game.Player1 : _game.Player2;
                var creature = _game.ActivePlayer.Hand[_summonHandIndex].Card;
                if (index >= 0 && index < summonOwner.BattleZone.Count
                    && _game.IsLegalOnPlayTarget(creature, _game.ActivePlayer, summonOwner, index))
                {
                    var hand = _summonHandIndex;
                    Safe(() =>
                    {
                        _game.SummonCreature(hand, summonOwner, index);
                        PlaySummonFx(creature);
                    });
                }
                else if (BoardCardAt(isBottomSide, index) is { } summonLook)
                {
                    ShowLookPopup(summonLook);
                }
                break;
            }

            case Mode.SelectEvolveTarget when _evolveHandIndex >= 0:
            {
                var evolveOwner = isBottomSide ? _game.Player1 : _game.Player2;
                var evolveCard = _game.ActivePlayer.Hand[_evolveHandIndex].Card;
                if (index >= 0 && index < evolveOwner.BattleZone.Count
                    && _game.IsLegalOnPlayTarget(evolveCard, _game.ActivePlayer, evolveOwner, index))
                {
                    _pendingEvolveTarget = new SpellTarget(evolveOwner, index);
                    StartEvolveBaseSelection(_evolveHandIndex);
                }
                else if (BoardCardAt(isBottomSide, index) is { } evolveLook)
                {
                    ShowLookPopup(evolveLook);
                }
                break;
            }

            case Mode.EvolveBase when _evolveHandIndex >= 0:
            {
                var baseOwner = isBottomSide ? _game.Player1 : _game.Player2;
                var evolveCard = _game.ActivePlayer.Hand[_evolveHandIndex].Card;
                if (SideIsActive(isBottomSide) && index >= 0 && index < baseOwner.BattleZone.Count
                    && DuelGame.IsEvolutionBase(evolveCard, baseOwner.BattleZone[index].Card))
                {
                    var hand = _evolveHandIndex;
                    var evolveTarget = _pendingEvolveTarget;
                    Safe(() =>
                    {
                        if (evolveTarget is { } t)
                            _game.EvolveCreature(hand, index, t.Owner, t.Index);
                        else
                            _game.EvolveCreature(hand, index);
                        PlaySummonFx(evolveCard);
                    });
                }
                else if (BoardCardAt(isBottomSide, index) is { } evolveBaseLook)
                {
                    ShowLookPopup(evolveBaseLook);
                }
                break;
            }

            case Mode.SelectShieldTarget when _triggerHandIndex >= 0:
            {
                var triggerOwner = _game.ShieldTriggerOwner!;
                var targetOwner = isBottomSide ? _game.Player1 : _game.Player2;
                var spell = triggerOwner.Hand[_triggerHandIndex].Card;
                if (index >= 0 && index < targetOwner.BattleZone.Count
                    && _game.IsLegalSpellTarget(spell, triggerOwner, targetOwner, index))
                {
                    var hand = _triggerHandIndex;
                    Safe(() => _game.PlayShieldTrigger(hand, targetOwner, index));
                }
                else if (BoardCardAt(isBottomSide, index) is { } triggerLook)
                {
                    ShowLookPopup(triggerLook);
                }
                break;
            }

            case Mode.SelectBlock:
                if (!SideIsActive(isBottomSide) && IsDefenderBlocker(index))
                {
                    Safe(() =>
                    {
                        PlayAttackFx(_attackerIndex, BattleInstanceCenter(_game.Opponent.BattleZone[index]));
                        _game.AttackPlayer(_attackerIndex, _game.Opponent, index);
                    });
                    ResetInteraction();
                }
                break;
        }
        Refresh();
    }

    /// <summary>
    /// Mark one of the active player's own mana cards as used (tap) or unused
    /// (untap) this turn. This is the physical "tap the mana you spend" gesture:
    /// tapped mana is excluded by <see cref="DuelGame.CanAfford"/> and <see cref="DuelGame.PayManaFor"/>,
    /// and the engine untaps all of it again at the start of the owner's next turn.
    /// </summary>
    private void OnManaClicked(bool isBottomSide, int index)
    {
        if (_game is null || _game.IsGameOver)
            return;
        if (_mode == Mode.SelectBlock)
            return;
        if (_awaitingBlockChoice)
            return;
        if (_game!.ShieldTriggerWindowActive || _mode is Mode.SelectSpellTarget or Mode.SelectShieldTarget)
        {
            // Targeting (spell targets / trigger decisions) and trigger interrupts
            // only allow inspecting mana cards, never toggling them.
            if (ManaCardAt(isBottomSide, index) is { } idleMana)
                ShowLookPopup(idleMana);
            return;
        }
        if (!CanAct || !SideIsActive(isBottomSide))
        {
            // Clicking mana with no action available (enemy mana, opponent's turn)
            // just lets the player inspect the card.
            if (ManaCardAt(isBottomSide, index) is { } idleMana)
                ShowLookPopup(idleMana);
            return;
        }
        if (index < 0 || index >= _game.ActivePlayer.ManaZone.Count)
            return;

        var mana = _game.ActivePlayer.ManaZone[index];
        if (mana.IsTapped)
        {
            mana.Untap();
            Notice("Mana untapped (available again this turn).");
        }
        else
        {
            mana.Tap();
            Notice("Mana tapped (used for this turn). It untaps at your next turn.");
        }
        Refresh();
    }

    private string AiAttackerName(int index)
    {
        if (_game is null || index < 0 || index >= _game.ActivePlayer.BattleZone.Count)
            return "The AI";
        return _game.ActivePlayer.BattleZone[index].Card.Name;
    }

    private bool IsDefenderBlocker(int index)
    {
        var defender = _game.Opponent;
        if (index < 0 || index >= defender.BattleZone.Count)
            return false;
        if (!defender.BattleZone[index].Card.HasKeyword(Keyword.Blocker) || defender.BattleZone[index].IsTapped)
            return false;
        Card attackerCard;
        if (_awaitingBlockChoice)
        {
            if (_pendingAiAttackerIndex < 0 || _pendingAiAttackerIndex >= _game.ActivePlayer.BattleZone.Count)
                return false;
            attackerCard = _game.ActivePlayer.BattleZone[_pendingAiAttackerIndex].Card;
        }
        else
        {
            if (_attackerIndex < 0 || _attackerIndex >= _game.ActivePlayer.BattleZone.Count)
                return false;
            attackerCard = _game.ActivePlayer.BattleZone[_attackerIndex].Card;
        }
        return DuelGame.CanBeBlocked(attackerCard);
    }

    private void OnShieldsClicked(bool isBottomSide, int shieldIndex = -1)
    {
        if (_game is null || _game.IsGameOver)
            return;
        if (_game.ShieldTriggerWindowActive)
            return;

        // "Choose a shield and look at it" tap ability: the next click on the
        // active player's own shield cards peeks at that shield (it stays put).
        if (_mode == Mode.SelectShieldPeek)
        {
            if (!SideIsActive(isBottomSide))
            {
                Notice("The tap ability needs one of YOUR OWN shields - click the shields you keep.");
                return;
            }
            if (shieldIndex < 0 || shieldIndex >= _game.ActivePlayer.Shields.Count)
            {
                Notice("Click one of your shield cards to look at it.");
                return;
            }
            var creature = _game.ActivePlayer.BattleZone.ElementAtOrDefault(_tapCreatureIndex);
            if (creature is null)
            {
                ResetInteraction();
                Refresh();
                return;
            }
            var peeked = _game.ActivePlayer.Shields[shieldIndex];
            Safe(() =>
            {
                _game.ActivateTapAbilityShield(_tapCreatureIndex, shieldIndex);
                PlayTapFx(creature);
            });
            ResetInteraction();
            ShowLookPopup(peeked);
            Prompt($"You looked at shield #{shieldIndex + 1}: {peeked.Name} ({peeked.Civilization}). It stays exactly where it was.");
            Refresh();
            return;
        }

        if (_awaitingBlockChoice)
        {
            // The human defender accepts the hit without blocking.
            if (!SideIsActive(isBottomSide))
            {
                Safe(() =>
                {
                    PlayAttackFx(_pendingAiAttackerIndex);
                    _game.AttackPlayer(_pendingAiAttackerIndex);
                    _awaitingBlockChoice = false;
                    ResumeAi();
                });
                ResetInteraction();
                Refresh();
            }
            return;
        }

        if (!CanAct)
            return;

        // The active player attacks the DEFENDER's shields.
        if (!SideIsActive(isBottomSide))
        {
            if (_mode == Mode.Idle)
            {
                // The empty shield zone is only a target once an attacker is chosen;
                // say so instead of silently swallowing the click.
                Prompt(_game.Opponent.ShieldCount == 0
                    ? "The enemy has no shields left! Pick one of your UNTAPPED creatures, then click the enemy's shields zone to land the final attack and win."
                    : "Pick one of your ready creatures first: click it, then click the enemy's shields zone to attack.");
                return;
            }

            if (_mode is Mode.SelectTarget or Mode.SelectHand)
            {
                if (_attackerIndex < 0)
                {
                    Prompt("Select one of your ready creatures to attack first.");
                    return;
                }

                if (OpponentHasEligibleBlocker(_game.ActivePlayer.BattleZone[_attackerIndex].Card))
                {
                    if (_vsAi && _ai is not null)
                    {
                        // The AI defends for itself: decide whether to block.
                        var chosen = _ai.DecideBlock(_game, _attackerIndex, out var blockerIdx);
                        Safe(() =>
                        {
                            if (chosen)
                            {
                                PlayAttackFx(_attackerIndex, BattleInstanceCenter(_game.Opponent.BattleZone[blockerIdx]));
                                _game.AttackPlayer(_attackerIndex, _game.Opponent, blockerIdx);
                            }
                            else
                            {
                                PlayAttackFx(_attackerIndex);
                                _game.AttackPlayer(_attackerIndex);
                            }
                        });
                        ResetInteraction();
                        Refresh();
                        return;
                    }

_mode = Mode.SelectBlock;
                    HideHandPopup();
                    HideLookPopup();
                    Prompt($"{_game.ActivePlayer.BattleZone[_attackerIndex].Card.Name} attacks! The defender may block: click a Blocker creature, or click the shields to take the hit.");
                    return;
                }

                Safe(() =>
                {
                    PlayAttackFx(_attackerIndex);
                    _game.AttackPlayer(_attackerIndex);
                });
                Refresh();
                return;
            }

            if (_mode == Mode.SelectBlock && _attackerIndex >= 0)
            {
                Safe(() =>
                {
                    PlayAttackFx(_attackerIndex);
                    _game.AttackPlayer(_attackerIndex);
                });
                Refresh();
            }
        }
    }

    private bool OpponentHasEligibleBlocker(Card attackerCard) =>
        DuelGame.CanBeBlocked(attackerCard)
        && _game.Opponent.BattleZone.Any(c => c.Card.IsCreature && !c.IsTapped && c.Card.HasKeyword(Keyword.Blocker));

    // ------------------------------------------------------------ AI driving

    private void ResumeAi()
    {
        _aiDriving = true;
        _aiTimer = AiStepDelay;
    }

    /// <summary>
    /// After an AI step error, advances the engine past the AI's turn so the human can
    /// keep playing; never leaves the board stuck mid-AI-turn.
    /// </summary>
    private void EndAiTurnGracefully()
    {
        _aiDriving = false;
        if (_game is null || _game.IsGameOver || !ReferenceEquals(_game.ActivePlayer, _ai?.Self))
            return;
        try
        {
            _game.EndMainPhase();
            _game.EndTurn();
            if (!_game.IsGameOver)
            {
                _game.StartTurn();
                _game.Draw();
            }
        }
        catch (RuleViolationException)
        {
            // The engine may already be in a partial state; stop driving either way.
        }
        ResetInteraction();
    }

    public override void _Process(double delta)
    {
        if (!_aiDriving || _game is null || _awaitingBlockChoice)
            return;
        if (_game.IsGameOver)
        {
            _aiDriving = false;
            Refresh();
            return;
        }
        if (_game.ShieldTriggerWindowActive)
            return; // paused while a defender resolves shield triggers
        if (_game.Phase != GamePhase.Main)
            return;

        _aiTimer -= (float)delta;
        if (_aiTimer > 0f)
            return;
        _aiTimer = AiStepDelay;

        AiStep step;
        try
        {
            CaptureFx();
            step = _ai!.Step(_game);
        }
        catch (RuleViolationException ex)
        {
            // The AI should never violate the rules; if it does, end its turn
            // gracefully and hand control back to the human instead of soft-locking.
            Notice($"AI error: {ex.Message}");
            EndAiTurnGracefully();
            Refresh();
            return;
        }

        switch (step.Kind)
        {
            case AiStepKind.ActionTaken:
                break;

            case AiStepKind.NeedsBlockChoice:
                _awaitingBlockChoice = true;
                _pendingAiAttackerIndex = step.AttackerIndex;
                _mode = Mode.SelectBlock;
                Prompt($"{AiAttackerName(step.AttackerIndex)} attacks your shields! Click a Blocker creature to intercept, or click your shields to take the hit.");
                break;

            case AiStepKind.TurnEnded:
                _aiDriving = false;
                try
                {
                    CaptureFx();
                    _game.EndMainPhase();
                    _game.EndTurn();
                    if (!_game.IsGameOver)
                    {
                        _game.StartTurn();
                        _game.Draw();
                    }
                }
                catch (RuleViolationException ex)
                {
                    Notice(ex.Message);
                }
                ResetInteraction();
                break;
        }

        Refresh();
    }

    // ------------------------------------------------------------ ui helpers

    private void Safe(Action action)
    {
        CaptureFx();
        try
        {
            action();
            ResetInteraction();
            // After a successful attack, say that more creatures can still charge in
            // and attack too (each untapped creature may attack once per turn).
            if (!_game.IsGameOver && _game.Phase == GamePhase.Main && _game.HasAttackedThisTurn
                && (_ai is null || !ReferenceEquals(_game.ActivePlayer, _ai.Self))
                && _game.ActivePlayer.BattleZone.Any(c => !c.IsTapped && !c.IsSummoningSick))
            {
                Prompt("You can still attack: click another untapped creature, then the enemy shields (or a tapped enemy creature).");
            }
        }
        catch (RuleViolationException ex)
        {
            Notice(ex.Message);
        }
        Refresh();
    }

    private void ResetInteraction()
    {
        _mode = Mode.Idle;
        _attackerIndex = -1;
        _selectedHandSide = false;
        _selectedHandIndex = -1;
        _spellHandIndex = -1;
        _triggerHandIndex = -1;
        _summonHandIndex = -1;
        _evolveHandIndex = -1;
        _pendingEvolveTarget = null;
        _tapCreatureIndex = -1;
        _maxTargets = 0;
        _spellTargetPicks.Clear();
        HideHandPopup();
        HideLookPopup();
        HideAttackMenu();
        HideTapTargetMenu();
        HideTriggerPopup();
        HideScryPopup();
    }

    private void Prompt(string message)
    {
        _promptLabel.Text = message;
        _promptLabel.AddThemeColorOverride("font_color", UiStyles.AccentText);
    }

    private void Notice(string message)
    {
        _promptLabel.Text = message;
        _promptLabel.AddThemeColorOverride("font_color", message.Length == 0 ? UiStyles.BodyText : UiStyles.ErrorText);
    }

    private void Refresh()
    {
        if (_game is null)
        {
            _turnLabel.Text = "";
            _endTurn.Disabled = true;
            _newDuelBtn.Disabled = false;
            return;
        }

        // Shield-trigger windows owned by the AI resolve instantly (no popup);
        // human-owned windows are handled by the decision popup after the zones
        // are rebuilt below.
        if (_game.ShieldTriggerWindowActive && _vsAi && _ai is not null
            && ReferenceEquals(_game.ShieldTriggerOwner, _game.Player2))
        {
            _ai.ResolveShieldTriggers(_game);
        }

        RecomputeCardSize();

        // Fixed seats: the player (Player 1) always sits at the bottom with a face-up
        // hand; the opponent (Player 2 / AI) always at the top with a face-down hand
        // (revealed only by the debug toggle, or in hotseat the shared screen shows it).
        var revealOpponent = _vsAi ? GameSettings.RevealAiHand : true;

        BuildZoneInto(_bottomHand, _game.Player1.Hand, backs: false, artOnly: true, _bottomHandTitle, CardSizeKind.Full);
        BuildZoneInto(_topHand, _game.Player2.Hand, backs: !revealOpponent, artOnly: revealOpponent, _topHandTitle, revealOpponent ? CardSizeKind.Mana : CardSizeKind.Stack);
        BuildZoneInto(_bottomBattle, _game.Player1.BattleZone, backs: false, artOnly: true, _bottomBattleTitle, CardSizeKind.Full);
        BuildZoneInto(_topBattle, _game.Player2.BattleZone, backs: false, artOnly: true, _topBattleTitle, CardSizeKind.Full);
        BuildZoneInto(_bottomMana, _game.Player1.ManaZone, backs: false, artOnly: true, _bottomManaTitle, CardSizeKind.Mana, ManaTapStaggerSeconds);
        BuildZoneInto(_topMana, _game.Player2.ManaZone, backs: false, artOnly: true, _topManaTitle, CardSizeKind.Mana, ManaTapStaggerSeconds);
        BuildShields(_bottomShields, _game.Player1.ShieldCount, _bottomShieldsTitle);
        BuildShields(_topShields, _game.Player2.ShieldCount, _topShieldsTitle);

        UpdatePile(_bottomDeckPile, _bottomDeckLabel, faceUp: false, null, "DECK", _game.Player1.Deck.Count);
        UpdatePile(_bottomGravePile, _bottomGraveLabel, faceUp: true, _game.Player1.Graveyard.LastOrDefault()?.Card, "GRAVE", _game.Player1.Graveyard.Count, () => ShowGraveyard(_game.Player1));
        UpdatePile(_topDeckPile, _topDeckLabel, faceUp: false, null, "DECK", _game.Player2.Deck.Count);
        UpdatePile(_topGravePile, _topGraveLabel, faceUp: true, _game.Player2.Graveyard.LastOrDefault()?.Card, "GRAVE", _game.Player2.Graveyard.Count, () => ShowGraveyard(_game.Player2));

        var isYou = ReferenceEquals(_game.ActivePlayer, _game.Player1) && _vsAi;
        var who = isYou ? "You" : _game.ActivePlayer.Name;
        var turnText = isYou ? "Your turn" : $"{who}'s turn";
        var noShields = "";
        if (_game.Player1.ShieldCount == 0 || _game.Player2.ShieldCount == 0)
        {
            var endangered = _game.Player1.ShieldCount == 0 && _game.Player2.ShieldCount == 0
                ? "Both players"
                : _game.Player1.ShieldCount == 0
                    ? (_game.Player1.Name == "You" ? "You" : _game.Player1.Name)
                    : _game.Player2.Name;
            noShields = $"  |  {endangered} has no shields - the next direct attack wins!";
        }
        _turnLabel.Text = _game.IsGameOver
            ? $"Game over - {_game.Winner!.Name} wins!"
            : $"{turnText}  |  Turn {_game.TurnNumber}  |  {_game.Phase}" + (_aiDriving ? "  [AI thinking...]" : "") + noShields;

        if (_game.IsGameOver && !_winnerShown)
        {
            _winnerShown = true;
            _fxManager?.BigWin();
            Sfx.Play(SfxId.Win);
            ShowWinnerBanner(_game.Winner!.Name);
        }

        _endTurn.Disabled = _game.IsGameOver || !CanAct || _game.Phase == GamePhase.End;
        if (!_game.IsGameOver && _game.Phase == GamePhase.End)
            _endTurn.Disabled = true;
        _takeHitBtn.Visible = _awaitingBlockChoice;

        WireInteraction();
        SyncShieldTriggerPopup();
        SyncScryPopup();

        CapturePrevTapped();
        CallDeferred(nameof(PlayFx));

        // After a human-owned trigger window closes mid-AI-turn, hand the drive back.
        if (!_game.ShieldTriggerWindowActive && !_aiDriving && _vsAi && _ai is not null
            && ReferenceEquals(_game.ActivePlayer, _ai.Self)
            && !_game.IsGameOver && _game.Phase == GamePhase.Main)
            ResumeAi();
    }

    private void BuildZoneInto(VBoxContainer box, IReadOnlyList<CardInstance> zone, bool backs, bool artOnly, Label title, CardSizeKind kind, float tapStaggerSeconds = 0f)
    {
        var flow = GetFlow(box);
        ClearFlow(flow);
        for (var i = 0; i < zone.Count; i++)
        {
            var inst = zone[i];
            var view = new CardView(inst.Card, backs ? null : ArtFor(inst.Card), faceDown: backs, artOnly: artOnly);
            ApplyCardSize(view, kind);
            view.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            flow.AddChild(view);
            if (_prevTapped.TryGetValue(inst, out var wasTapped) && wasTapped != inst.IsTapped)
            {
                // Pose at the old state, then stagger the tap/untap transition so
                // several cards flip visibly one after another (mainly the mana zone).
                if (tapStaggerSeconds > 0f)
                    view.AnimateFromTappedAfter(tapStaggerSeconds * i, wasTapped, inst.IsTapped);
                else
                    view.AnimateFromTapped(wasTapped, inst.IsTapped);
            }
            else
                view.SnapTapped(inst.IsTapped);
        }
        title.Text = $"{CaptionOf(box)}  ({zone.Count})";
    }

    private void BuildShields(VBoxContainer box, int count, Label title)
    {
        var flow = GetFlow(box);
        ClearFlow(flow);

        // When shields are zero the flow holds no shield cards, so there is nothing
        // to click for the final (winning) direct attack. Provide a real click target,
        // a strip with an explicit minimum size, because a child of a container gets
        // its rect from its minimum size: an empty Control is 0x0 and invisible to the
        // mouse. With shields on the field the shield cards themselves deliver the
        // click (see WireZoneCards), so the strip is only needed for the empty zone.
        var isBottomSide = ReferenceEquals(box, _bottomShields);
        var existing = isBottomSide ? _bottomShieldsStrip : _topShieldsStrip;
        if (existing is not null && IsInstanceValid(existing))
            existing.QueueFree();
        if (isBottomSide)
            _bottomShieldsStrip = null;
        else
            _topShieldsStrip = null;

        if (count == 0)
        {
            var strip = new Control
            {
                MouseFilter = Control.MouseFilterEnum.Stop,
                CustomMinimumSize = new Vector2(168, 56),
            };
            strip.Name = "ShieldStripClickCatcher";
            strip.GuiInput += @event =>
            {
                if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
                    OnShieldsClicked(isBottomSide);
            };
            var hint = new Label
            {
                Text = "NO SHIELDS",
                MouseFilter = Control.MouseFilterEnum.Ignore,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            hint.AddThemeFontSizeOverride("font_size", 11);
            hint.AddThemeColorOverride("font_color", UiStyles.BodyText);
            hint.SetAnchorsPreset(LayoutPreset.FullRect);
            strip.AddChild(hint);
            flow.AddChild(strip);
            if (isBottomSide)
                _bottomShieldsStrip = strip;
            else
                _topShieldsStrip = strip;
        }

        for (var i = 0; i < count; i++)
        {
            var view = new CardView(null, faceDown: true);
            ApplyCardSize(view, CardSizeKind.Stack);
            view.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            flow.AddChild(view);
        }
        title.Text = $"{CaptionOf(box)}  ({count})";
    }

    private void ApplyCardSize(CardView view, CardSizeKind kind)
    {
        switch (kind)
        {
            case CardSizeKind.Full:
                view.SetCardSize(_cardW, _cardH);
                break;
            case CardSizeKind.Mana:
                view.SetCardSize(_manaW, _manaH);
                break;
            default:
                view.SetCardSize(_stackW, _stackH);
                break;
        }
    }

    private static void ClearFlow(BoxContainer flow)
    {
        // Detach immediately (then free at end of frame). Building a new zone
        // happens in the SAME frame via Refresh -> BuildZoneInto -> WireInteraction,
        // so any GetChildren() traversal must only see the freshly added cards. A
        // plain QueueFree leaves the old generation visible to GetChildren() until
        // the end of the frame, which re-indexes clicks and mis-applies selection.
        foreach (var child in flow.GetChildren().OfType<Node>().ToList())
        {
            flow.RemoveChild(child);
            child.QueueFree();
        }
    }

    private static string CaptionOf(VBoxContainer box)
    {
        foreach (var child in box.GetChildren())
            if (child is Label l && l.Text.Length > 0)
                return l.Text.Split("  (").First();
        return "";
    }

    private void WireInteraction()
    {
        WireZoneCards(_bottomHand, isHand: true, isBottom: true);
        WireZoneCards(_topHand, isHand: true, isBottom: false);
        WireZoneCards(_bottomBattle, isBottom: true);
        WireZoneCards(_topBattle, isBottom: false);
        WireZoneCards(_bottomMana, isMana: true, isBottom: true);
        WireZoneCards(_topMana, isMana: true, isBottom: false);
        WireZoneCards(_bottomShields, isShields: true, isBottom: true);
        WireZoneCards(_topShields, isShields: true, isBottom: false);
    }

    private void WireZoneCards(VBoxContainer box, bool isBottom = false, bool isHand = false, bool isShields = false, bool isMana = false)
    {
        var flow = GetFlow(box);
        var views = flow.GetChildren().OfType<CardView>().ToList();
        for (var i = 0; i < views.Count; i++)
        {
            var idx = i;
            var side = isBottom;
            if (isHand)
            {
                var isSelected = (_mode == Mode.SelectHand || _mode == Mode.SelectSpellTarget)
                    && side == _selectedHandSide && idx == _selectedHandIndex;
                views[i].SetSelected(isSelected);
                views[i].Clicked += _ => OnHandClicked(side, idx);
            }
            else if (isShields)
                views[i].Clicked += _ => OnShieldsClicked(side, idx);
            else if (isMana)
                views[i].Clicked += _ => OnManaClicked(side, idx);
            else
            {
                views[i].Clicked += _ => OnBattleClicked(side, idx);
                views[i].SetSelected(IsTargetCandidate(side, idx));
                ApplyRoleBadge(views[i], CombatRoleAt(side, idx));
            }
        }
    }

    private Card? BoardCardAt(bool isBottomSide, int index)
    {
        if (_game is null)
            return null;
        var zone = (isBottomSide ? _game.Player1 : _game.Player2).BattleZone;
        return index >= 0 && index < zone.Count ? zone[index].Card : null;
    }

    /// <summary>
    /// The current combat role of a battle-zone card while the player is choosing an
    /// attack target or a blocker: the attacker itself (ATTACK), a legal tapped
    /// creature target while aiming (TARGET), or a ready Blocker that can intercept
    /// the pending attack (BLOCK). Rendered as a small ribbon over the card and used
    /// by the prompts, so the attacking monster and the attacked/blocking monsters
    /// are always visible in the information block.
    /// </summary>
    private CombatRole? CombatRoleAt(bool isBottomSide, int index)
    {
        if (_game is null || index < 0)
            return null;
        var owner = isBottomSide ? _game.Player1 : _game.Player2;
        if (index >= owner.BattleZone.Count)
            return null;

        if (SideIsActive(isBottomSide))
        {
            var attackerIndex = _awaitingBlockChoice ? _pendingAiAttackerIndex : _attackerIndex;
            if (_mode is Mode.SelectTarget or Mode.SelectBlock && attackerIndex == index)
                return CombatRole.Attack;
            if (_awaitingBlockChoice && _pendingAiAttackerIndex == index)
                return CombatRole.Attack;
            return null;
        }

        if (_mode == Mode.SelectTarget)
            return owner.BattleZone[index].IsTapped ? CombatRole.Target : null;
        if (_mode == Mode.SelectBlock || _awaitingBlockChoice)
            return IsDefenderBlocker(index) ? CombatRole.Block : null;
        return null;
    }

    private void ApplyRoleBadge(CardView view, CombatRole? role)
    {
        switch (role)
        {
            case CombatRole.Attack:
                view.SetCombatBadge("ATTACK", AttackTint);
                break;
            case CombatRole.Target:
                view.SetCombatBadge("TARGET", TargetTint);
                break;
            case CombatRole.Block:
                view.SetCombatBadge("BLOCK", BlockTint);
                break;
            default:
                view.SetCombatBadge(null, Colors.White);
                break;
        }
    }

    // ------------------------------------------------- zone transition effects

    /// <summary>
    /// Records enough UI state (hand/deck counts, every battle-zone card's on-screen
    /// centre keyed by instance) to play draw/destroy animations after the next
    /// Refresh rebuilds the board. Called immediately before a game mutation.
    /// </summary>
    private void CaptureFx()
    {
        if (_game is null)
        {
            _fx = null;
            return;
        }
        var fx = new FxSnapshot();
        fx.HandCounts[0] = _game.Player1.Hand.Count;
        fx.HandCounts[1] = _game.Player2.Hand.Count;
        fx.DeckCounts[0] = _game.Player1.Deck.Count;
        fx.DeckCounts[1] = _game.Player2.Deck.Count;
        CaptureBattlePositions(fx, _game.Player1, _bottomBattle);
        CaptureBattlePositions(fx, _game.Player2, _topBattle);
        CaptureShieldPositions(fx, 0, _bottomShields);
        CaptureShieldPositions(fx, 1, _topShields);
        _fx = fx;
    }

    private static void CaptureShieldPositions(FxSnapshot fx, int playerNo, VBoxContainer box)
    {
        var pos = new List<Vector2>();
        foreach (var view in GetFlow(box).GetChildren().OfType<CardView>())
            pos.Add(view.GetGlobalRect().GetCenter());
        fx.ShieldPos[playerNo] = pos;
    }

    private static void CaptureBattlePositions(FxSnapshot fx, Player p, VBoxContainer box)
    {
        var views = GetFlow(box).GetChildren().OfType<CardView>().ToList();
        var n = Math.Min(views.Count, p.BattleZone.Count);
        for (var i = 0; i < n; i++)
            fx.BattlePos[p.BattleZone[i]] = views[i].GetGlobalRect().GetCenter();
    }

    /// <summary>
    /// Plays the pending transition animations captured before the last action.
    /// Runs deferred (next frame) from Refresh so the freshly rebuilt zones have
    /// been laid out and report correct on-screen positions.
    /// </summary>
    private void PlayFx()
    {
        if (_game is null || _fx is null)
        {
            _fx = null;
            return;
        }
        var fx = _fx;
        _fx = null;

        PlayDestroyFx(fx);
        PlayDrawFx(fx);
        PlayShieldToHandFx(fx);
    }

    private void PlayDestroyFx(FxSnapshot fx)
    {
        var any = false;
        if (PlayGraveFx(fx, _bottomGravePile, _game.Player1, out var at0))
        {
            any = true;
            _fxManager?.Burst(at0, new Color(0.62f, 0.55f, 0.78f, 1f), 14, 0.5f);
        }
        if (PlayGraveFx(fx, _topGravePile, _game.Player2, out var at1))
        {
            any = true;
            _fxManager?.Burst(at1, new Color(0.62f, 0.55f, 0.78f, 1f), 14, 0.5f);
        }
        if (any)
            Sfx.Play(SfxId.Destroy);
    }

    private bool PlayGraveFx(FxSnapshot fx, VBoxContainer pile, Player p, out Vector2 at)
    {
        var to = PileFaceCenter(pile);
        var any = false;
        for (var i = 0; i < p.Graveyard.Count; i++)
        {
            var inst = p.Graveyard[i];
            if (!fx.BattlePos.TryGetValue(inst, out var from))
                continue; // not a battle->grave transition we witnessed
            SpawnFly(inst.Card, faceUp: true, from, to, delay: 0f, fadeOut: true, swell: true);
            any = true;
        }
        at = to;
        return any;
    }

    private void PlayDrawFx(FxSnapshot fx)
    {
        PlayDrawFxFor(fx, playerNo: 0, _game.Player1, _bottomDeckPile, _bottomHand, faceUp: true);
        PlayDrawFxFor(fx, playerNo: 1, _game.Player2, _topDeckPile, _topHand,
            faceUp: !_vsAi || GameSettings.RevealAiHand);
    }

    private void PlayDrawFxFor(FxSnapshot fx, int playerNo, Player p, VBoxContainer deckPile, VBoxContainer handBox, bool faceUp)
    {
        // A pure draw: the deck lost exactly as many cards as the hand gained.
        // (Shield breaks also add to hand but don't shrink the deck, so they are
        // excluded automatically and stay un-animated.)
        var preDeck = fx.DeckCounts.GetValueOrDefault(playerNo);
        var preHand = fx.HandCounts.GetValueOrDefault(playerNo);
        var deckLoss = preDeck - p.Deck.Count;
        var handGain = p.Hand.Count - preHand;
        if (deckLoss <= 0 || deckLoss != handGain)
            return;
        Sfx.Play(SfxId.Draw);

        var from = PileFaceCenter(deckPile);
        var to = HandFlowCenter(handBox);
        for (var k = 0; k < deckLoss; k++)
        {
            var idx = preHand + k;
            if (idx < 0 || idx >= p.Hand.Count)
                continue;
            SpawnFly(p.Hand[idx].Card, faceUp, from, to, delay: k * 0.11f, fadeOut: false, swell: false);
        }
    }

    /// <summary>
    /// Flies the broken shields from their shield-zone spot into the defender's hand.
    /// Each broken shield lifts the first (top) shield card, so the k-th broken card
    /// departs from the k-th recorded shield position. Draws between pre-capture and
    /// now are excluded because a shield break adds to the hand without shrinking the
    /// deck, so the top few new hand cards were exactly the broken shields.
    /// </summary>
    private void PlayShieldToHandFx(FxSnapshot fx)
    {
        PlayShieldToHandFxFor(fx, 0, _game.Player1, _bottomHand);
        PlayShieldToHandFxFor(fx, 1, _game.Player2, _topHand);
    }

    private void PlayShieldToHandFxFor(FxSnapshot fx, int playerNo, Player p, VBoxContainer handBox)
    {
        var preShieldPos = fx.ShieldPos.GetValueOrDefault(playerNo);
        if (preShieldPos is null || preShieldPos.Count == 0)
            return;
        var broken = preShieldPos.Count - p.ShieldCount;
        if (broken <= 0)
            return;
        _fxManager?.ShieldShatter(ShieldZoneCenter(p), new Color("ffd25a"));
        Sfx.Play(SfxId.ShieldBreak);
        var preHand = fx.HandCounts.GetValueOrDefault(playerNo);
        var to = HandFlowCenter(handBox);
        var faceUp = playerNo == 0 || !_vsAi || GameSettings.RevealAiHand;
        for (var k = 0; k < broken; k++)
        {
            var from = k < preShieldPos.Count ? preShieldPos[k] : HandFlowCenter(handBox);
            var idx = preHand + k;
            if (idx < 0 || idx >= p.Hand.Count)
                continue;
            SpawnFly(p.Hand[idx].Card, faceUp, from, to, delay: k * 0.11f, fadeOut: false, swell: true);
        }
    }

    // ------------------------------------------------------------- Phase 5 (FX)

    /// <summary>
    /// The attack beat: a civilization-colored beam from the attacker's lane to its
    /// target (a tapped enemy creature, or the defender's shield zone for a direct
    /// attack) plus the metallic "shing". Runs against the pre-action layout.
    /// </summary>
    private void PlayAttackFx(int attackerIndex, Vector2? target = null)
    {
        if (_game is null || attackerIndex < 0 || attackerIndex >= _game.ActivePlayer.BattleZone.Count)
            return;
        var attacker = _game.ActivePlayer.BattleZone[attackerIndex];
        var from = BattleInstanceCenter(attacker);
        var to = target ?? ShieldZoneCenter(_game.Opponent);
        _fxManager?.Beam(from, to, CivilizationPalette.Color(attacker.Card.Civilization));
        Sfx.Play(SfxId.Attack);
    }

    private void PlayCastFx(Card spell)
    {
        if (_game is null)
            return;
        Sfx.Play(SfxId.Cast);
        _fxManager?.Ping(BattleZoneFlowCenter(_game.ActivePlayer), CivilizationPalette.Color(spell.Civilization), 0.35f);
    }

    private void PlaySummonFx(Card creature)
    {
        if (_game is null)
            return;
        Sfx.Play(SfxId.Summon);
        _fxManager?.Ping(BattleZoneFlowCenter(_game.ActivePlayer), CivilizationPalette.Color(creature.Civilization));
    }

    private void PlayTapFx(CardInstance inst)
    {
        if (_game is null)
            return;
        Sfx.Play(SfxId.Tap);
        _fxManager?.Ping(BattleInstanceCenter(inst), CivilizationPalette.Color(inst.Card.Civilization), 0.28f);
    }

    /// <summary>Center of a creature's lane card, or the battle zone's flow centre fallback.</summary>
    private Vector2 BattleInstanceCenter(CardInstance inst)
    {
        if (_game is null)
            return Vector2.Zero;
        var (player, box) = ReferenceEquals(inst.Owner, _game.Player1)
            ? (_game.Player1, _bottomBattle)
            : (_game.Player2, _topBattle);
        var views = GetFlow(box).GetChildren().OfType<CardView>().ToList();
        var i = player.BattleZone.IndexOf(inst);
        return i >= 0 && i < views.Count ? views[i].GetGlobalRect().GetCenter() : box.GetGlobalRect().GetCenter();
    }

    private Vector2 ShieldZoneCenter(Player p)
    {
        if (_game is null)
            return Vector2.Zero;
        return (ReferenceEquals(p, _game.Player1) ? _bottomShields : _topShields).GetGlobalRect().GetCenter();
    }

    private Vector2 BattleZoneFlowCenter(Player p)
    {
        if (_game is null)
            return Vector2.Zero;
        return (ReferenceEquals(p, _game.Player1) ? _bottomBattle : _topBattle).GetGlobalRect().GetCenter();
    }

    /// <summary>One-shot golden "X wins!" banner, eased in and auto-hidden.</summary>
    private void ShowWinnerBanner(string winnerName)
    {
        _winnerBanner.Text = $"{winnerName} wins!";
        _winnerBanner.Visible = true;
        _winnerBanner.Modulate = new Color(1f, 1f, 1f, 0f);
        _winnerBanner.Scale = Vector2.One * 0.6f;
        var tween = _winnerBanner.CreateTween();
        tween.Parallel().TweenProperty(_winnerBanner, "modulate:a", 1f, 0.45d);
        tween.Parallel()
            .TweenProperty(_winnerBanner, "scale", Vector2.One, 0.55d)
            .SetTrans(Tween.TransitionType.Back)
            .SetEase(Tween.EaseType.Out);
        tween.Finished += () =>
        {
            var t2 = _winnerBanner.CreateTween();
            t2.TweenInterval(2.4d);
            t2.TweenProperty(_winnerBanner, "modulate:a", 0f, 0.6d);
            t2.Finished += () => _winnerBanner.Visible = false;
        };
    }

    private static Vector2 PileFaceCenter(VBoxContainer pile)
    {
        var face = pile.GetChildren().OfType<Panel>().FirstOrDefault();
        if (face is null)
            return pile.GetGlobalRect().GetCenter();
        return face.GetGlobalRect().GetCenter();
    }

    private static Vector2 HandFlowCenter(VBoxContainer handBox) =>
        GetFlow(handBox).GetGlobalRect().GetCenter();

    /// <summary>
    /// Spawns a transient "ghost" card on the animation layer that flies from
    /// <paramref name="from"/> to <paramref name="to"/> (global positions) and frees
    /// itself. Optional: fade out the arriving card (destroy) or swell it slightly
    /// during flight (destroy drama). Mouse-transparent. With <paramref name="faceUp"/>
    /// false the ghost shows the card back (hidden draws).
    /// </summary>
    private void SpawnFly(Card card, bool faceUp, Vector2 from, Vector2 to, float delay, bool fadeOut, bool swell)
    {
        var w = _cardW;
        var h = _cardH;
        var ghost = new Control
        {
            CustomMinimumSize = new Vector2(w, h),
            Size = new Vector2(w, h),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _fxLayer.AddChild(ghost);
        ghost.GlobalPosition = from - new Vector2(w * 0.5f, h * 0.5f);

        var frame = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        frame.SetAnchorsPreset(LayoutPreset.FullRect);
        frame.AddThemeStyleboxOverride("panel", MakeGhostStyle(CivilizationPalette.Color(card.Civilization)));
        ghost.AddChild(frame);

        var tex = faceUp ? LoadArt(ArtFor(card)) : LoadArt("res://assets/art/cards/BackCard.webp");
        if (tex is not null)
        {
            var img = new TextureRect
            {
                Texture = tex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            img.SetAnchorsPreset(LayoutPreset.FullRect);
            frame.AddChild(img);
        }

        var tw = ghost.CreateTween();
        if (delay > 0f)
            tw.TweenInterval(delay);
        tw.SetParallel(true);
        tw.TweenProperty(ghost, "global_position", to - new Vector2(w * 0.5f, h * 0.5f), 0.42f)
            .SetTrans(Tween.TransitionType.Quad)
            .SetEase(Tween.EaseType.InOut);
        if (swell)
            tw.TweenProperty(ghost, "scale", Vector2.One * 1.07f, 0.42f);
        if (fadeOut)
            tw.TweenProperty(ghost, "modulate:a", 0f, 0.42f);
        tw.Finished += ghost.QueueFree;
    }

    private static Texture2D? LoadArt(string? path)
    {
        if (string.IsNullOrEmpty(path) || !ResourceLoader.Exists(path))
            return null;
        return ResourceLoader.Load<Texture2D>(path);
    }

    private static StyleBoxFlat MakeGhostStyle(Color civ)
    {
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(civ, 0.9f),
            BorderColor = civ.Lightened(0.35f),
            CornerRadiusTopLeft = 3,
            CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3,
            CornerRadiusBottomRight = 3,
        };
        sb.SetBorderWidthAll(2);
        return sb;
    }

    // ------------------------------------------------------- tap pose tracking

    private void CapturePrevTapped()
    {
        if (_game is null)
            return;
        CapturePrevTappedZone(_game.Player1.Hand);
        CapturePrevTappedZone(_game.Player1.ManaZone);
        CapturePrevTappedZone(_game.Player1.BattleZone);
        CapturePrevTappedZone(_game.Player2.Hand);
        CapturePrevTappedZone(_game.Player2.ManaZone);
        CapturePrevTappedZone(_game.Player2.BattleZone);
    }

    private void CapturePrevTappedZone(IReadOnlyList<CardInstance> zone)
    {
        foreach (var inst in zone)
            _prevTapped[inst] = inst.IsTapped;
    }

    /// <summary>
    /// True while a spell-targeting state is active and the clicked battle-zone card
    /// is a legal target for the spell being aimed. Highlights make the board read
    /// as "clickable" instead of forcing the player to remember the targeting rules.
    /// </summary>
    private bool IsTargetCandidate(bool isBottomSide, int index)
    {
        if (_game is null || index < 0)
            return false;
        if (_mode == Mode.EvolveBase && _evolveHandIndex >= 0 && _evolveHandIndex < _game.ActivePlayer.Hand.Count)
        {
            var evolution = _game.ActivePlayer.Hand[_evolveHandIndex].Card;
            var baseOwner = isBottomSide ? _game.Player1 : _game.Player2;
            return SideIsActive(isBottomSide) && index < baseOwner.BattleZone.Count
                && DuelGame.IsEvolutionBase(evolution, baseOwner.BattleZone[index].Card);
        }
        if (_mode == Mode.SelectEvolveTarget && _evolveHandIndex >= 0)
        {
            if (_evolveHandIndex >= _game.ActivePlayer.Hand.Count)
                return false;
            var evolution = _game.ActivePlayer.Hand[_evolveHandIndex].Card;
            var evolveOwner = isBottomSide ? _game.Player1 : _game.Player2;
            return index < evolveOwner.BattleZone.Count
                && _game.IsLegalOnPlayTarget(evolution, _game.ActivePlayer, evolveOwner, index);
        }
        if (_mode == Mode.SelectSummonTarget)
        {
            if (_summonHandIndex < 0 || _summonHandIndex >= _game.ActivePlayer.Hand.Count)
                return false;
            var creature = _game.ActivePlayer.Hand[_summonHandIndex].Card;
            var summonOwner = isBottomSide ? _game.Player1 : _game.Player2;
            return index < summonOwner.BattleZone.Count
                && _game.IsLegalOnPlayTarget(creature, _game.ActivePlayer, summonOwner, index);
        }
        if (_mode != Mode.SelectSpellTarget && _mode != Mode.SelectShieldTarget)
            return false;

        Card? spell;
        Player owner;
        if (_mode == Mode.SelectShieldTarget)
        {
            owner = _game.ShieldTriggerOwner!;
            spell = _triggerHandIndex >= 0 && _triggerHandIndex < owner.Hand.Count
                ? owner.Hand[_triggerHandIndex].Card
                : null;
        }
        else
        {
            owner = _game.ActivePlayer;
            spell = _spellHandIndex >= 0 && _spellHandIndex < owner.Hand.Count
                ? owner.Hand[_spellHandIndex].Card
                : null;
        }
        if (spell is null)
            return false;

        var targetOwner = isBottomSide ? _game.Player1 : _game.Player2;
        return index < targetOwner.BattleZone.Count
            && _game.IsLegalSpellTarget(spell, owner, targetOwner, index);
    }

    private Card? ManaCardAt(bool isBottomSide, int index)
    {
        if (_game is null)
            return null;
        var zone = (isBottomSide ? _game.Player1 : _game.Player2).ManaZone;
        return index >= 0 && index < zone.Count ? zone[index].Card : null;
    }

    private string? ArtFor(Card card) => _artByCardId.TryGetValue(card.Id, out var p) ? p : null;
}