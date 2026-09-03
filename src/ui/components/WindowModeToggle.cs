using System;
using Godot;

namespace DuelMasters.UI.Components;

/// <summary>
/// A development helper button pinned to the bottom-right of a screen that toggles the
/// OS window between a "windowed" size of <see cref="WindowedSize"/> (1280x720) and a
/// "fullscreen" size of <see cref="FullscreenSize"/> (1920x1080), always staying in a
/// normal (non-mode) window and re-centering on the current screen.
///
/// "Fullscreen" here is intended as a 1920x1080 window, not the OS exclusive-fullscreen
/// window mode, so the toggle only ever resizes and repositions the window.
/// </summary>
[GlobalClass]
public partial class WindowModeToggle : Button
{
    /// <summary>The size used for the "windowed" view.</summary>
    public static readonly Vector2I WindowedSize = new(1280, 720);

    /// <summary>The size used for the "fullscreen" view.</summary>
    public static readonly Vector2I FullscreenSize = new(1920, 1080);

    private const float CornerMargin = 16f;

    public WindowModeToggle()
    {
        Text = "";
        TooltipText = "Toggle the window between 1280x720 and 1920x1080 (dev)";
        PivotOffset = Vector2.Zero;
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.BottomRight);
        OffsetLeft = -170f;
        OffsetTop = -44f;
        OffsetRight = -CornerMargin;
        OffsetBottom = -CornerMargin;
        Pressed += OnPressed;

        // Reflect the real state on startup, whichever size the window launches with.
        SyncLabel();
    }

    private void OnPressed()
    {
        var window = GetWindow();
        if (window is null)
            return;

        // Running inside the editor's embedded game view: the OS window cannot be
        // resized or moved there, so any resize would fail silently (or log "Embedded
        // window can't be moved"). Surface that instead of pretending to switch.
        if (window.IsEmbedded())
        {
            SyncLabel();
            GD.Print("[WindowModeToggle] Cannot resize window: the game is running in the editor's embedded view. Run the project as a standalone window (disable the embedded view) to toggle 1280x720 / 1920x1080.");
            return;
        }

        // Read the actual current size rather than trusting a cached flag, so the
        // button can never get out of sync with the real window size.
        var isFull = IsAtSize(window, FullscreenSize);

        window.Size = isFull ? WindowedSize : FullscreenSize;
        CenterOnCurrentScreen(window);

        SyncLabel();
    }

    private static bool IsAtSize(Window window, Vector2I size)
        => Math.Abs(window.Size.X - size.X) <= 2 && Math.Abs(window.Size.Y - size.Y) <= 2;

    private static void CenterOnCurrentScreen(Window window)
    {
        var screen = window.CurrentScreen;
        if (screen < 0 || screen >= DisplayServer.GetScreenCount())
            screen = DisplayServer.GetPrimaryScreen();

        var usable = DisplayServer.ScreenGetUsableRect(screen);
        window.Position = usable.Position + (usable.Size - window.Size) / 2;
    }

    /// <summary>Re-sync the button label to the current window size (or the embedded hint).</summary>
    public void SyncLabel()
    {
        var window = GetWindow();

        if (window is not null && window.IsEmbedded())
        {
            Text = "Run standalone to resize";
            TooltipText = "The editor's embedded game view can't be resized. Run the project as a standalone window (disable the embedded view) to toggle 1280x720 / 1920x1080.";
            return;
        }

        var isFull = window is not null && IsAtSize(window, FullscreenSize);
        Text = isFull ? "Windowed (1280x720)" : "Fullscreen (1920x1080)";
        TooltipText = isFull
            ? "Switch back to a 1280x720 windowed view"
            : "Switch the window to a 1920x1080 fullscreen view";
    }
}
