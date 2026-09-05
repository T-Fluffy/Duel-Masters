using System;
using System.Collections.Generic;
using System.Linq;
using DuelMasters.Core;
using DuelMasters.Domain;
using DuelMasters.Domain.Ai;
using DuelMasters.Gameplay.CardView;
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
    private enum Mode { Idle, SelectHand, SelectTarget, SelectBlock }

    private enum CardSizeKind { Full, Mana, Stack }

    private const string MainMenuPath = "res://src/ui/main_menu/MainMenu.tscn";
    private const float AiStepDelay = 0.55f;

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

    // Hand card popup ("Look at card" / "Play card") shown above the selected card.
    private PanelContainer _handPopup = null!;
    private VBoxContainer _handPopupBox = null!;
    private PanelContainer _lookPopup = null!;
    private VBoxContainer _lookPopupBox = null!;

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

        if (card.IsCreature && !_game.HasAttackedThisTurn)
        {
            var summon = new Button { Text = "Summon", Disabled = !_game.CanPlay(player, card) };
            summon.Pressed += () => DoSummon(index);
            _handPopupBox.AddChild(summon);
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

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event.IsActionPressed("ui_cancel"))
        {
            if (_inspectOverlay.Visible)
            {
                CloseInspect();
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
            _game.AttackPlayer(_pendingAiAttackerIndex);
            _awaitingBlockChoice = false;
            ResumeAi();
        });
    }

    private static string DescribeCard(Card card)
    {
        if (card.IsCreature)
            return $"Creature  -  {card.ManaCost} mana  -  {card.Power} power\nCivilization: {card.Civilization}";
        return $"Spell  -  {card.ManaCost} mana\nCivilization: {card.Civilization}";
    }

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
    private void UpdatePile(VBoxContainer pile, Label caption, bool faceUp, Card? topCard, string label, int count)
    {
        // The pile VBox holds [caption, face]; rebuild the face each refresh so the top
        // card / count always reflects the live state.
        foreach (var child in pile.GetChildren().OfType<Control>().Where(c => c is not Label).ToList())
        {
            pile.RemoveChild(child);
            child.QueueFree();
        }

        var face = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        face.CustomMinimumSize = new Vector2(_stackW, _stackH);
        face.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        face.AddThemeStyleboxOverride("panel", PileFaceStyle());

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

    private void DoCharge(int index) => Safe(() => _game.PlayManaToManaZone(index));

    private void DoSummon(int index)
    {
        Safe(() =>
        {
            var summoned = _game.SummonCreature(index);
            if (_game.IsGameOver || _game.Winner is not null)
                Notice($"{summoned.Card.Name} summoned.");
        });
    }

    private void DoCast(int index) => Safe(() => _game.CastSpell(index));

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
                        return;
                    }
                    _attackerIndex = index;
                    _mode = Mode.SelectTarget;
                    HideHandPopup();
                    HideLookPopup();
                    Prompt("Choose a target: a tapped enemy creature, or the enemy shields. Click the enemy zone to attack.");
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
                    var candidate = _game.ActivePlayer.BattleZone[index];
                    if (!candidate.IsTapped && !candidate.IsSummoningSick)
                    {
                        _attackerIndex = index;
                        Prompt("Pick a target for the new attacker.");
                    }
                    break;
                }
                var target = _game.Opponent.BattleZone[index];
                if (target.IsTapped)
                {
                    Safe(() => _game.AttackCreature(_attackerIndex, index));
                    ResetInteraction();
                }
                else
                {
                    ShowLookPopup(target.Card);
                }
                break;

            case Mode.SelectBlock:
                if (!SideIsActive(isBottomSide) && IsDefenderBlocker(index))
                {
                    Safe(() => _game.AttackPlayer(_attackerIndex, _game.Opponent, index));
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

    private bool IsDefenderBlocker(int index)
    {
        var defender = _game.Opponent;
        return index >= 0 && index < defender.BattleZone.Count
            && defender.BattleZone[index].Card.HasKeyword(Keyword.Blocker)
            && !defender.BattleZone[index].IsTapped;
    }

    private void OnShieldsClicked(bool isBottomSide)
    {
        if (_game is null || _game.IsGameOver)
            return;

        if (_awaitingBlockChoice)
        {
            // The human defender accepts the hit without blocking.
            if (!SideIsActive(isBottomSide))
            {
                Safe(() =>
                {
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
            if (_mode is Mode.SelectTarget or Mode.SelectHand)
            {
                if (_attackerIndex < 0)
                {
                    Prompt("Select one of your ready creatures to attack first.");
                    return;
                }

                if (OpponentHasEligibleBlocker())
                {
                    if (_vsAi && _ai is not null)
                    {
                        // The AI defends for itself: decide whether to block.
                        var chosen = _ai.DecideBlock(_game, _attackerIndex, out var blockerIdx);
                        Safe(() =>
                        {
                            if (chosen)
                                _game.AttackPlayer(_attackerIndex, _game.Opponent, blockerIdx);
                            else
                                _game.AttackPlayer(_attackerIndex);
                        });
                        ResetInteraction();
                        Refresh();
                        return;
                    }

                    _mode = Mode.SelectBlock;
                    HideHandPopup();
                    HideLookPopup();
                    Prompt("The defender may block. Click a Blocker creature, or click the shields again to attack anyway.");
                    return;
                }

                Safe(() => _game.AttackPlayer(_attackerIndex));
                Refresh();
                return;
            }

            if (_mode == Mode.SelectBlock && _attackerIndex >= 0)
            {
                Safe(() => _game.AttackPlayer(_attackerIndex));
                Refresh();
            }
        }
    }

    private bool OpponentHasEligibleBlocker() =>
        _game.Opponent.BattleZone.Any(c => c.Card.IsCreature && !c.IsTapped && c.Card.HasKeyword(Keyword.Blocker));

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
        if (_game.Phase != GamePhase.Main)
            return;

        _aiTimer -= (float)delta;
        if (_aiTimer > 0f)
            return;
        _aiTimer = AiStepDelay;

        AiStep step;
        try
        {
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
                Prompt("The AI attacks your shields! Click a Blocker creature to intercept, or click your shields to take the hit.");
                break;

            case AiStepKind.TurnEnded:
                _aiDriving = false;
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
        try
        {
            action();
            ResetInteraction();
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
        HideHandPopup();
        HideLookPopup();
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

        RecomputeCardSize();

        // Fixed seats: the player (Player 1) always sits at the bottom with a face-up
        // hand; the opponent (Player 2 / AI) always at the top with a face-down hand
        // (revealed only by the debug toggle, or in hotseat the shared screen shows it).
        var revealOpponent = _vsAi ? GameSettings.RevealAiHand : true;

        BuildZoneInto(_bottomHand, _game.Player1.Hand, backs: false, artOnly: true, _bottomHandTitle, CardSizeKind.Full);
        BuildZoneInto(_topHand, _game.Player2.Hand, backs: !revealOpponent, artOnly: revealOpponent, _topHandTitle, revealOpponent ? CardSizeKind.Mana : CardSizeKind.Stack);
        BuildZoneInto(_bottomBattle, _game.Player1.BattleZone, backs: false, artOnly: true, _bottomBattleTitle, CardSizeKind.Full);
        BuildZoneInto(_topBattle, _game.Player2.BattleZone, backs: false, artOnly: true, _topBattleTitle, CardSizeKind.Full);
        BuildZoneInto(_bottomMana, _game.Player1.ManaZone, backs: false, artOnly: true, _bottomManaTitle, CardSizeKind.Mana);
        BuildZoneInto(_topMana, _game.Player2.ManaZone, backs: false, artOnly: true, _topManaTitle, CardSizeKind.Mana);
        BuildShields(_bottomShields, _game.Player1.ShieldCount, _bottomShieldsTitle);
        BuildShields(_topShields, _game.Player2.ShieldCount, _topShieldsTitle);

        UpdatePile(_bottomDeckPile, _bottomDeckLabel, faceUp: false, null, "DECK", _game.Player1.Deck.Count);
        UpdatePile(_bottomGravePile, _bottomGraveLabel, faceUp: true, _game.Player1.Graveyard.LastOrDefault()?.Card, "GRAVE", _game.Player1.Graveyard.Count);
        UpdatePile(_topDeckPile, _topDeckLabel, faceUp: false, null, "DECK", _game.Player2.Deck.Count);
        UpdatePile(_topGravePile, _topGraveLabel, faceUp: true, _game.Player2.Graveyard.LastOrDefault()?.Card, "GRAVE", _game.Player2.Graveyard.Count);

        var who = $"{(ReferenceEquals(_game.ActivePlayer, _game.Player1) && _vsAi ? "You" : _game.ActivePlayer.Name)}";
        _turnLabel.Text = _game.IsGameOver
            ? $"Game over - {_game.Winner!.Name} wins!"
            : $"{who}'s turn  |  Turn {_game.TurnNumber}  |  {_game.Phase}" + (_aiDriving ? "  [AI thinking...]" : "");

        _endTurn.Disabled = _game.IsGameOver || !CanAct || _game.Phase == GamePhase.End;
        if (!_game.IsGameOver && _game.Phase == GamePhase.End)
            _endTurn.Disabled = true;
        _takeHitBtn.Visible = _awaitingBlockChoice;

        WireInteraction();
    }

    private void BuildZoneInto(VBoxContainer box, IReadOnlyList<CardInstance> zone, bool backs, bool artOnly, Label title, CardSizeKind kind)
    {
        var flow = GetFlow(box);
        ClearFlow(flow);
        for (var i = 0; i < zone.Count; i++)
        {
            var inst = zone[i];
            var view = new CardView(inst.Card, backs ? null : ArtFor(inst.Card), faceDown: backs, artOnly: artOnly);
            ApplyCardSize(view, kind);
            view.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            view.SnapTapped(inst.IsTapped);
            flow.AddChild(view);
        }
        title.Text = $"{CaptionOf(box)}  ({zone.Count})";
    }

    private void BuildShields(VBoxContainer box, int count, Label title)
    {
        var flow = GetFlow(box);
        ClearFlow(flow);
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
                var isSelected = _mode == Mode.SelectHand && side == _selectedHandSide && idx == _selectedHandIndex;
                views[i].SetSelected(isSelected);
                views[i].Clicked += _ => OnHandClicked(side, idx);
            }
            else if (isShields)
                views[i].Clicked += _ => OnShieldsClicked(side);
            else if (isMana)
                views[i].Clicked += _ => OnManaClicked(side, idx);
            else
                views[i].Clicked += _ => OnBattleClicked(side, idx);
        }
    }

    private Card? BoardCardAt(bool isBottomSide, int index)
    {
        if (_game is null)
            return null;
        var zone = (isBottomSide ? _game.Player1 : _game.Player2).BattleZone;
        return index >= 0 && index < zone.Count ? zone[index].Card : null;
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