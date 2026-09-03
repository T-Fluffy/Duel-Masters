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
///
/// When the game runs inside the editor's embedded game view there is no real OS window,
/// so the display server refuses to resize/move it (logs "Embedded window can't be
/// resized/moved"). <c>Window.IsEmbedded()</c> is unreliable here, so after attempting a
/// resize this compares the real size and, if it didn't change, explains that the window
/// is embedded and must be run as a standalone window to resize.
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
            return;

        var wid = window.GetWindowId();
        var before = DisplayServer.WindowGetSize(wid);
        var isFull = IsAtSize(before, FullscreenSize);
        var target = isFull ? WindowedSize : FullscreenSize;

        DisplayServer.WindowSetSize(target, wid);
        CenterOnCurrentScreen(wid, target);

        // Keep Godot's node state in sync with the (attempted) real window size.
        window.Size = target;

        var after = DisplayServer.WindowGetSize(wid);

        if (!IsAtSize(after, target))
        {
            // The display server refused the resize. This happens when the game runs in
            // the editor's embedded game view: there is no real OS window to resize.
            GD.Print($"[WindowModeToggle] Resize refused: {before} -> {target} (still {after}). " +
                     "The game is running in the editor's EMBEDDED game view, which cannot be resized. " +
                     "Run the project as a standalone window (terminal: godot --path .) to toggle size.");
        }
        else
        {
            GD.Print($"[WindowModeToggle] Resized {before} -> {after}.");
        }

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

    /// <summary>Re-sync the button label to the current window size.</summary>
    public void SyncLabel()
    {
        var window = GetWindow();
        if (window is null)
            return;

        var isFull = IsAtSize(DisplayServer.WindowGetSize(window.GetWindowId()), FullscreenSize);
        Text = isFull ? "Windowed (1280x720)" : "Fullscreen (1920x1080)";
        TooltipText = isFull
            ? "Switch back to a 1280x720 windowed view"
            : "Switch the window to a 1920x1080 fullscreen view";
    }
}
