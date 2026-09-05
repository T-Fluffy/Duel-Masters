using System;
using System.Collections.Generic;
using System.Linq;
using DuelMasters.Domain;
using DuelMasters.Resources;
using Godot;

namespace DuelMasters.Gameplay.CardView;

/// <summary>
/// A single interactive card on the 2.5D board. Renders either a face-down card
/// back or the card front (artwork only by default, or a full frame for the
/// "artOnly=false" callers), and scales smoothly toward its hover/selection target
/// via exponential smoothing in <see cref="_Process"/>. Tapped cards stay upright
/// and are gently dimmed so their layout rect doubles as their click rect.
///
/// Input model: the card root keeps <see cref="Control.MouseFilter"/> = Stop and
/// EVERY visual child is mouse-transparent (Ignore), so a click always lands on the
/// card itself and <see cref="Clicked"/> fires from its <c>gui_input</c>. The face
/// is redrawn with proportional offsets/fonts whenever the layout size changes, so
/// the drawn card exactly fills its layout rect at any size (no shrink transform).
/// </summary>
public partial class CardView : Control
{
    private const float CardWidth = 140f;
    private const float CardHeight = 195f;
    private const float HoverScale = 1.25f;
    private const float SelectedScale = 1.12f;
    private const float ScaleSpeed = 12f;
    private const float RotateSpeed = 10f;
    private const float TapAngle = Mathf.Pi / 2f;
    private static readonly Color TappedDim = new(0.78f, 0.78f, 0.8f);

    private Vector2 _currentScale = Vector2.One;
    private bool _hovered;
    private bool _selected;

    private float _width;
    private float _height;
    private bool _artOnly;
    private bool _faceDown;

    private Panel _frame = null!;
    private Panel? _selectionBox;

    /// <summary>The domain card (null means a face-down card back).</summary>
    public Card? Card { get; }

    /// <summary>True while this card is rendered face-down.</summary>
    public bool FaceDown => Card is null;

    /// <summary>Raised when the player left-clicks the card.</summary>
    public event Action<CardView>? Clicked;

    public bool Tapped { get; private set; }

    /// <summary>
    /// Creates a card view. With <paramref name="artOnly"/> set the front is the
    /// clean "hand" look: artwork only, no header/text/footer clutter.
    /// </summary>
    public CardView(Card? card, string? artPath = null, bool faceDown = false, bool artOnly = false)
    {
        Card = faceDown ? null : card;
        _artOnly = artOnly;
        _faceDown = faceDown || card is null;
        _artPath = artPath;
        _width = CardWidth;
        _height = CardHeight;
        CustomMinimumSize = new Vector2(_width, _height);
        Size = new Vector2(_width, _height);
        PivotOffset = new Vector2(_width / 2f, _height / 2f);
        MouseFilter = MouseFilterEnum.Stop;
        BuildFace();
        MakeFaceTransparent(this);
    }

    /// <summary>
    /// Highlight this card as the currently selected hand/board card. Selection
    /// also nudges the card up slightly (via <see cref="_Process"/>) so the picked
    /// vs unpicked hand cards read instantly.
    /// </summary>
    public void SetSelected(bool selected)
    {
        _selected = selected;
        if (_selectionBox is not null)
            _selectionBox.Visible = selected;
    }

    /// <summary>
    /// Resize this card in place. The face is rebuilt with proportional offsets and
    /// font sizes so it exactly fills the <paramref name="width"/>×<paramref name="height"/>
    /// layout rect at any size - the drawn card occupies the whole box with the art
    /// never cropped. The hover pivot follows the new center.
    /// </summary>
    public float CardScale => _faceScale;

    private float _faceScale = 1f;

    public void SetCardSize(float width, float height)
    {
        if (width <= 0f || height <= 0f)
            return;
        _width = width;
        _height = height;
        CustomMinimumSize = new Vector2(_width, _height);
        Size = new Vector2(_width, _height);
        PivotOffset = new Vector2(_width / 2f, _height / 2f);
        _faceScale = Math.Min(_width / CardWidth, _height / CardHeight);
        RebuildFace();
    }

    private void RebuildFace()
    {
        foreach (var child in GetChildren().OfType<Control>().ToList())
        {
            RemoveChild(child);
            child.QueueFree();
        }
        _frame = null!;
        _selectionBox = null;

        BuildFace();
        MakeFaceTransparent(this);

        if (Tapped)
            SetTapped(true);
        SetSelected(_selected);
    }

    private void BuildFace()
    {
        var frame = new Panel();
        frame.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(frame);
        _frame = frame;

        if (_faceDown)
        {
            var backTex = LoadArt("res://assets/art/cards/BackCard.webp");
            if (backTex is not null)
            {
                var backImg = new TextureRect
                {
                    Texture = backTex,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                };
                backImg.SetAnchorsPreset(LayoutPreset.FullRect);
                frame.AddChild(backImg);
            }
            else
            {
                frame.AddThemeStyleboxOverride("panel", MakeStyle(new Color(0.08f, 0.10f, 0.16f), new Color(0.20f, 0.24f, 0.34f)));
                var mark = new Label
                {
                    Text = "DM",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    SizeFlagsVertical = SizeFlags.ExpandFill,
                };
                mark.AddThemeFontSizeOverride("font_size", Sf(42));
                mark.AddThemeColorOverride("font_color", new Color(0.45f, 0.52f, 0.68f));
                frame.AddChild(mark);
            }

            BuildExtras();
            return;
        }

        var card = Card;
        var civ = CivilizationPalette.Color(card!.Civilization);
        var bg = new Color(civ, 0.92f);
        frame.AddThemeStyleboxOverride("panel", MakeStyle(bg.Darkened(0.25f), civ.Lightened(0.35f), 3f));

        var s = _faceScale;
        if (_artOnly)
        {
            // Clean "in hand" look: artwork fills the whole card, no text clutter.
            var artBox = new Panel
            {
                AnchorLeft = 0.03f,
                AnchorTop = 0.03f,
                AnchorRight = 0.97f,
                AnchorBottom = 0.97f,
            };
            artBox.AddThemeStyleboxOverride("panel", MakeStyle(new Color(0.04f, 0.04f, 0.06f), civ.Lightened(0.3f), 3f));
            AddArt(card, artBox, civ, _artPath);
            frame.AddChild(artBox);
        }
        else
        {
            var header = new Panel
            {
                AnchorLeft = 0f,
                AnchorTop = 0f,
                AnchorRight = 1f,
                AnchorBottom = 0f,
                OffsetBottom = S(38),
            };
            header.AddThemeStyleboxOverride("panel", MakeStyle(civ, civ.Lightened(0.2f), 0f));
            frame.AddChild(header);

            var cost = new Label
            {
                Text = card.ManaCost.ToString(),
                OffsetLeft = S(6),
                OffsetTop = S(4),
                OffsetRight = S(30),
                OffsetBottom = S(32),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            cost.AddThemeFontSizeOverride("font_size", Sf(24));
            cost.AddThemeColorOverride("font_color", new Color(0, 0, 0));
            frame.AddChild(cost);

            var name = new Label
            {
                Text = System.Text.RegularExpressions.Regex.Replace(card.Name, @"(.{10})", "$1\n").TrimEnd('\n'),
                OffsetLeft = S(34),
                OffsetTop = S(2),
                OffsetRight = S(-4),
                OffsetBottom = S(36),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            name.AddThemeFontSizeOverride("font_size", Sf(13));
            name.AddThemeColorOverride("font_color", new Color(0, 0, 0, 0.85f));
            frame.AddChild(name);

            var artBox = new Panel
            {
                AnchorLeft = 0.06f,
                AnchorTop = 0.20f,
                AnchorRight = 0.94f,
                AnchorBottom = 0.56f,
            };
            artBox.AddThemeStyleboxOverride("panel", MakeStyle(new Color(0.05f, 0.05f, 0.07f), new Color(0.18f, 0.18f, 0.22f)));
            AddArt(card, artBox, civ, _artPath);
            frame.AddChild(artBox);

            // Rules-text box: race + keyword abilities, autowrapped and scaled with the
            // card so it stays readable at every render size (esp. the card inspector).
            var rules = new Label
            {
                AnchorLeft = 0.06f,
                AnchorTop = 0.58f,
                AnchorRight = 0.94f,
                AnchorBottom = 0.84f,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Text = RulesText(card),
            };
            rules.AddThemeFontSizeOverride("font_size", Sf(10));
            rules.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.92f));
            frame.AddChild(rules);

            var footer = new Panel
            {
                AnchorLeft = 0f,
                AnchorTop = 0.88f,
                AnchorRight = 1f,
                AnchorBottom = 1f,
            };
            footer.AddThemeStyleboxOverride("panel", MakeStyle(new Color(0, 0, 0, 0.35f), new Color(0, 0, 0, 0.5f)));
            frame.AddChild(footer);

            if (card.IsCreature)
            {
                var power = new Label
                {
                    Text = card.Power.ToString(),
                    OffsetLeft = S(-34),
                    OffsetTop = S(0),
                    OffsetRight = S(-6),
                    OffsetBottom = S(34),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                power.AddThemeFontSizeOverride("font_size", Sf(24));
                power.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.7f));
                footer.AddChild(power);
            }
        }

        BuildExtras();
    }

    private string? _artPath;

    private void AddArt(Card card, Control host, Color civ, string? artPath)
    {
        _artPath = artPath;
        var art = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
        };
        art.SetAnchorsPreset(LayoutPreset.FullRect);
        var tex = LoadArt(artPath);
        if (tex is not null)
        {
            art.Texture = tex;
        }
        else
        {
            art.Modulate = new Color(0.9f, 0.9f, 0.9f);
            var glyph = new Label
            {
                Text = CivGlyph(card.Civilization),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            glyph.SetAnchorsPreset(LayoutPreset.FullRect);
            glyph.AddThemeFontSizeOverride("font_size", Sf(64));
            glyph.AddThemeColorOverride("font_color", civ.Lightened(0.4f));
            art.AddChild(glyph);
        }
        host.AddChild(art);
    }

    /// <summary>
    /// Gold selection frame over every face-up/face-down card. Mouse-transparent so
    /// it never eats clicks.
    /// </summary>
    private void BuildExtras()
    {
        var s = _faceScale;
        var sel = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        sel.SetAnchorsPreset(LayoutPreset.FullRect);
        var selStyle = new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0f),
            BorderColor = new Color(1f, 0.82f, 0.18f),
            CornerRadiusTopLeft = 3,
            CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3,
            CornerRadiusBottomRight = 3,
        };
        selStyle.SetBorderWidthAll(Math.Max(2, (int)(4 * s + 0.5f)));
        sel.AddThemeStyleboxOverride("panel", selStyle);
        sel.Visible = false;
        AddChild(sel);
        _selectionBox = sel;
    }

    /// <summary>Makes every decorative child mouse-transparent so the card root always wins input.</summary>
    private static void MakeFaceTransparent(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is Control c)
                c.MouseFilter = MouseFilterEnum.Ignore;
            MakeFaceTransparent(child);
        }
    }

    private float S(float px) => px * _faceScale;

    private int Sf(int px) => Math.Max(5, (int)Math.Round(px * _faceScale, MidpointRounding.AwayFromZero));

    public override void _Ready()
    {
        MouseEntered += () => _hovered = true;
        MouseExited += () => _hovered = false;
        GuiInput += OnGuiInput;
    }

    private void OnGuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            Clicked?.Invoke(this);
    }

    /// <summary>
    /// Instantly snap the tap pose (no lerp). Tapped cards stay upright - they are
    /// gently dimmed so their layout rect doubles as their click rect. (Rotating the
    /// whole view shifted get_global_rect by the card height when pivot tracking was
    /// incomplete, breaking hit-testing.)
    /// </summary>
    public void SnapTapped(bool tapped)
    {
        Tapped = tapped;
        RefreshTapPose();
    }

    /// <summary>Set a new tap target (same upright pose as <see cref="SnapTapped"/>).</summary>
    public void SetTapped(bool tapped)
    {
        Tapped = tapped;
        RefreshTapPose();
    }

    private void RefreshTapPose()
    {
        _frame.SelfModulate = Tapped ? TappedDim : Colors.White;
    }

    public override void _Process(double delta)
    {
        // The face is laid out to exactly fill the layout rect (see SetCardSize), so
        // only hover and selection add scale on top of it.
        var hoverMult = _hovered ? HoverScale : 1f;
        var selectedMult = _selected ? SelectedScale : 1f;
        var target = Vector2.One * (hoverMult * selectedMult);
        _currentScale = _currentScale.Lerp(target, (float)(1f - Mathf.Exp(-ScaleSpeed * delta)));
        Scale = _currentScale;
    }

    private static Texture2D? LoadArt(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        return ResourceLoader.Exists(path) ? ResourceLoader.Load<Texture2D>(path) : null;
    }

    private static string RulesText(Card card)
    {
        var result = new List<string>();
        if (!string.IsNullOrEmpty(card.Race))
            result.Add(card.Race);

        if (card.HasKeyword(Keyword.Blocker)) result.Add("Blocker");
        if (card.HasKeyword(Keyword.ShieldTrigger)) result.Add("Shield trigger");
        if (card.HasKeyword(Keyword.SpeedAttacker)) result.Add("Speed attacker");
        if (card.HasKeyword(Keyword.Slayer)) result.Add("Slayer");
        if (card.HasKeyword(Keyword.PowerAttacker)) result.Add("Power attacker");
        if (card.HasKeyword(Keyword.TripleBreaker)) result.Add("Triple breaker");
        else if (card.HasKeyword(Keyword.DoubleBreaker)) result.Add("Double breaker");

        return string.Join("   ", result);
    }

    private static string CivGlyph(Civilization civ) => civ switch
    {
        Civilization.Light => "L",
        Civilization.Water => "W",
        Civilization.Darkness => "D",
        Civilization.Fire => "F",
        Civilization.Nature => "N",
        _ => "Z",
    };

    private static StyleBoxFlat MakeStyle(Color bg, Color border, float corner = 2f)
    {
        var sb = new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = border,
            CornerRadiusTopLeft = (int)(corner + 0.5f),
            CornerRadiusTopRight = (int)(corner + 0.5f),
            CornerRadiusBottomLeft = (int)(corner + 0.5f),
            CornerRadiusBottomRight = (int)(corner + 0.5f),
        };
        sb.SetBorderWidthAll(1);
        return sb;
    }
}