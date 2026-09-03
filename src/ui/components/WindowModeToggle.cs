using System;
using Godot;

namespace DuelMasters.UI.Components;

/// <summary>
/// A development helper button pinned to the bottom-right of a screen that toggles the
/// OS window between a "windowed" size of <see cref="WindowedSize"/> (1280x720) and a
/// "fullscreen" size of <see cref="FullscreenSize"/> (1920x1080), always staying in a
/// normal (non-mode) window and re-centering on the current screen.
///
/// "Fullscreen" is intended as a 1920x1080 window, not OS exclusive-fullscreen mode.
/// Resizing uses the raw DisplayServer calls with the explicit window id (the most
/// reliable channel), and diagnostics are printed so issues are easy to spot. When run
/// inside the editor's embedded game view the OS window cannot be resized, so it shows a
/// hint instead.
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
        SyncLabel();
    }

    private void OnPressed()
    {
        var window = GetWindow();
        if (window is null)
        {
            GD.Print("[WindowModeToggle] GetWindow() returned null - cannot resize.");
            return;
        }

        var wid = window.GetWindowId();
        var embedded = window.IsEmbedded();
        var before = DisplayServer.WindowGetSize(wid);
        GD.Print($"[WindowModeToggle] Clicked: windowId={wid} embedded={embedded} before={before}");

        // Running inside the editor's embedded game view: the OS window cannot be
        // resized or moved there, so any resize would fail silently.
        if (embedded)
        {
            SyncLabel();
            GD.Print("[WindowModeToggle] Cannot resize: game is running in the editor's embedded view. Run as a standalone window (disable the embedded game view) to toggle size.");
            return;
        }

        var isFull = IsAtSize(before, FullscreenSize);
        var target = isFull ? WindowedSize : FullscreenSize;

        DisplayServer.WindowSetSize(target, wid);
        CenterOnCurrentScreen(wid, target);

        // Sync the node so Godot's internal state matches the real window.
        window.Size = target;

        var after = DisplayServer.WindowGetSize(wid);
        GD.Print($"[WindowModeToggle] Applied target={target} after={after}");

        SyncLabel();
    }

    private static bool IsAtSize(Vector2I actual, Vector2I expected)
        => Math.Abs(actual.X - expected.X) <= 2 && Math.Abs(actual.Y - expected.Y) <= 2;

    private static void CenterOnCurrentScreen(int windowId, Vector2I size)
    {
        var screen = DisplayServer.WindowGetCurrentScreen(windowId);
        if (screen < 0 || screen >= DisplayServer.GetScreenCount())
            screen = DisplayServer.GetPrimaryScreen();

        var usable = DisplayServer.ScreenGetUsableRect(screen);
        var pos = usable.Position + (usable.Size - size) / 2;
        DisplayServer.WindowSetPosition(pos, windowId);
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

        var isFull = window is not null && IsAtSize(DisplayServer.WindowGetSize(window.GetWindowId()), FullscreenSize);
        Text = isFull ? "Windowed (1280x720)" : "Fullscreen (1920x1080)";
        TooltipText = isFull
            ? "Switch back to a 1280x720 windowed view"
            : "Switch the window to a 1920x1080 fullscreen view";
    }
}
