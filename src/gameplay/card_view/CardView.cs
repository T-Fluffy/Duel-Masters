using System;
using DuelMasters.Domain;
using DuelMasters.Resources;
using Godot;

namespace DuelMasters.Gameplay.CardView;

/// <summary>
/// A single interactive card on the 2.5D board. Renders either a face-down card
/// back or the card front (civilization-colored frame, mana cost, name, artwork
/// and power), and provides the two signature animations:
///   - hover scale: grows smoothly toward a target when the pointer is over it
///   - tap rotation: rotates 90 degrees (radians) toward the tapped/untapped pose
///
/// All animation is driven by exponential smoothing in <see cref="_Process"/> so
/// movement stays frame-rate independent and visually lerped.
/// </summary>
public partial class CardView : Control
{
    private const float CardWidth = 140f;
    private const float CardHeight = 195f;
    private const float HoverScale = 1.25f;
    private const float ScaleSpeed = 12f;
    private const float RotateSpeed = 10f;
    private const float TapAngle = Mathf.Pi / 2f;

    private Vector2 _currentScale = Vector2.One;
    private float _currentRotation;
    private bool _hovered;

    /// <summary>The domain card (null means a face-down card back).</summary>
    public Card? Card { get; }

    /// <summary>True while this card is rendered face-down.</summary>
    public bool FaceDown => Card is null;

    /// <summary>Raised when the player left-clicks the card.</summary>
    public event Action<CardView>? Clicked;

    public bool Tapped { get; private set; }

    public CardView(Card? card, string? artPath = null, bool faceDown = false)
    {
        Card = faceDown ? null : card;
        CustomMinimumSize = new Vector2(CardWidth, CardHeight);
        Size = new Vector2(CardWidth, CardHeight);
        PivotOffset = new Vector2(CardWidth / 2f, CardHeight / 2f);
        MouseFilter = MouseFilterEnum.Stop;
        BuildFace(faceDown || card is null, card, artPath);
    }

    private void BuildFace(bool back, Card? card, string? artPath)
    {
        var frame = new Panel();
        frame.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(frame);

        if (back)
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
            mark.AddThemeFontSizeOverride("font_size", 42);
            mark.AddThemeColorOverride("font_color", new Color(0.45f, 0.52f, 0.68f));
            frame.AddChild(mark);
            return;
        }

        if (card is null)
            return;

        var civ = CivilizationPalette.Color(card.Civilization);
        var bg = new Color(civ, 0.92f);
        frame.AddThemeStyleboxOverride("panel", MakeStyle(bg.Darkened(0.25f), civ.Lightened(0.35f), 3f));

        var header = new Panel
        {
            AnchorLeft = 0f,
            AnchorTop = 0f,
            AnchorRight = 1f,
            AnchorBottom = 0f,
            OffsetBottom = 38f,
        };
        header.AddThemeStyleboxOverride("panel", MakeStyle(civ, civ.Lightened(0.2f), 0f));
        frame.AddChild(header);

        var cost = new Label
        {
            Text = card.ManaCost.ToString(),
            OffsetLeft = 6f,
            OffsetTop = 4f,
            OffsetRight = 30f,
            OffsetBottom = 32f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        cost.AddThemeFontSizeOverride("font_size", 22);
        cost.AddThemeColorOverride("font_color", new Color(0, 0, 0));
        frame.AddChild(cost);

        var name = new Label
        {
            Text = System.Text.RegularExpressions.Regex.Replace(card.Name, @"(.{10})", "$1\n").TrimEnd('\n'),
            OffsetLeft = 34f,
            OffsetTop = 2f,
            OffsetRight = -4f,
            OffsetBottom = 36f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        name.AddThemeFontSizeOverride("font_size", 12);
        name.AddThemeColorOverride("font_color", new Color(0, 0, 0, 0.85f));
        frame.AddChild(name);

        var artBox = new Panel
        {
            AnchorLeft = 0.06f,
            AnchorTop = 0.22f,
            AnchorRight = 0.94f,
            AnchorBottom = 0.74f,
        };
        artBox.AddThemeStyleboxOverride("panel", MakeStyle(new Color(0.05f, 0.05f, 0.07f), new Color(0.18f, 0.18f, 0.22f)));
        frame.AddChild(artBox);

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
            glyph.AddThemeFontSizeOverride("font_size", 64);
            glyph.AddThemeColorOverride("font_color", civ.Lightened(0.4f));
            art.AddChild(glyph);
        }
        artBox.AddChild(art);

        var footer = new Panel
        {
            AnchorLeft = 0f,
            AnchorTop = 0.78f,
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
                OffsetLeft = -34f,
                OffsetTop = 0f,
                OffsetRight = -6f,
                OffsetBottom = 34f,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            power.AddThemeFontSizeOverride("font_size", 22);
            power.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.7f));
            footer.AddChild(power);
        }
    }

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
    /// Instantly snap the tap pose (no lerp). Used when rebuilding a zone so cards
    /// appear already at the correct rotation rather than animating from upright.
    /// </summary>
    public void SnapTapped(bool tapped)
    {
        Tapped = tapped;
        _currentRotation = tapped ? TapAngle : 0f;
        Rotation = _currentRotation;
    }

    /// <summary>Set a new tap target; the card rotates toward it over <see cref="RotateSpeed"/>.</summary>
    public void SetTapped(bool tapped)
    {
        Tapped = tapped;
    }

    public override void _Process(double delta)
    {
        var targetScale = _hovered ? Vector2.One * HoverScale : Vector2.One;
        _currentScale = _currentScale.Lerp(targetScale, (float)(1f - Mathf.Exp(-ScaleSpeed * delta)));
        Scale = _currentScale;

        var targetRotation = Tapped ? TapAngle : 0f;
        _currentRotation = Mathf.Lerp(_currentRotation, targetRotation, (float)(1f - Mathf.Exp(-RotateSpeed * delta)));
        if (Mathf.Abs(_currentRotation - targetRotation) < 0.001f)
            _currentRotation = targetRotation;
        Rotation = _currentRotation;
    }

    private static Texture2D? LoadArt(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        return ResourceLoader.Exists(path) ? ResourceLoader.Load<Texture2D>(path) : null;
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
