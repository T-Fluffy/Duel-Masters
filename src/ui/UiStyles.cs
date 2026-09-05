using Godot;

namespace DuelMasters.UI;

/// <summary>
/// Shared visual building blocks for overlay UI. The repo builds its panels in
/// code, and control-less default theme StyleBoxes render as near-transparent
/// washed-out boxes - these helpers guarantee every modal / popup / card gets a
/// solid, high-contrast dark surface so text stays readable over any scene.
/// </summary>
public static class UiStyles
{
    public const int CornerRadius = 8;

    /// <summary>Dim layer that covers the whole screen behind a modal.</summary>
    public static StyleBoxFlat ModalBackdrop() => new()
    {
        BgColor = new Color(0.01f, 0.02f, 0.04f, 0.93f),
    };

    /// <summary>Solid modal card with padding (labels/buttons sit on dark, readable).</summary>
    public static StyleBoxFlat ModalCard() => new()
    {
        BgColor = new Color(0.07f, 0.09f, 0.13f, 0.98f),
        BorderColor = new Color(0.35f, 0.42f, 0.55f, 1f),
        CornerRadiusTopLeft = CornerRadius,
        CornerRadiusTopRight = CornerRadius,
        CornerRadiusBottomLeft = CornerRadius,
        CornerRadiusBottomRight = CornerRadius,
        ContentMarginLeft = 28f,
        ContentMarginTop = 22f,
        ContentMarginRight = 28f,
        ContentMarginBottom = 22f,
    };

    /// <summary>Compact solid panel for popups (gear menu, tooltips).</summary>
    public static StyleBoxFlat Popup() => new()
    {
        BgColor = new Color(0.06f, 0.08f, 0.12f, 0.98f),
        BorderColor = new Color(0.35f, 0.42f, 0.55f, 1f),
        CornerRadiusTopLeft = 6,
        CornerRadiusTopRight = 6,
        CornerRadiusBottomLeft = 6,
        CornerRadiusBottomRight = 6,
        ContentMarginLeft = 6f,
        ContentMarginTop = 6f,
        ContentMarginRight = 6f,
        ContentMarginBottom = 6f,
    };

    /// <summary>Primary bright text for titles on the dark surfaces.</summary>
    public static readonly Color TitleText = new(1f, 1f, 1f);

    /// <summary>Secondary body text.</summary>
    public static readonly Color BodyText = new(0.84f, 0.88f, 0.95f);

    /// <summary>Muted helper text.</summary>
    public static readonly Color MutedText = new(0.58f, 0.63f, 0.72f);

    /// <summary>Error / warning red.</summary>
    public static readonly Color ErrorText = new(1f, 0.62f, 0.5f);

    /// <summary>Accent used for the active turn / status line.</summary>
    public static readonly Color AccentText = new(1f, 0.9f, 0.62f);
}