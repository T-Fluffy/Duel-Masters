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
/// so the display server refuses to resize/move it. <c>Window.IsEmbedded()</c> is
/// unreliable here (it can report <c>false</c> for an actually-embedded window), so the
/// first resize attempt is used to confirm. If the display server refuses, the button is
/// disabled and labelled clearly instead of leaving a dead, misleading control.
/// </summary>
[GlobalClass]
public partial class WindowModeToggle : Button
{
    /// <summary>The size used for the "windowed" view.</summary>
    public static readonly Vector2I WindowedSize = new(1280, 720);

    /// <summary>The size used for the "fullscreen" view.</summary>
    public static readonly Vector2I FullscreenSize = new(1920, 1080);

    private const float CornerMargin = 16f;

    private bool _embeddedConfirmed;

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

        // Best-effort early disable when IsEmbedded() is reliable; the click path covers
        // the cases where it wrongly reports false.
        CallDeferred(nameof(CheckEmbedded));
    }

    /// <summary>Whether the OS window is considered embedded by Godot's node API.</summary>
    public void CheckEmbedded()
    {
        var window = GetWindow();
        if (window is not null && window.IsEmbedded())
            SetEmbeddedState();
    }

    private void OnPressed()
    {
        var window = GetWindow();
        if (window is null)
            return;

        // Fast-path: if the node reports embedded, disable right away.
        if (window.IsEmbedded())
        {
            SetEmbeddedState();
            return;
        }

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
            GD.Print("[WindowModeToggle] Resize refused: " +
                     $"{before} -> {target} (still {after}). " +
                     "This is the editor's EMBEDDED game view, which cannot be resized. " +
                     "Run the project as a standalone window (run_game.bat) to toggle size.");
            SetEmbeddedState();
            return;
        }

        GD.Print($"[WindowModeToggle] Resized {before} -> {after}.");
        SyncLabel();
    }

    /// <summary>
    /// Truncate this unusable control now that the window is known to be embedded:
    /// disable it and label it clearly so it no longer looks like an actionable toggle.
    /// </summary>
    private void SetEmbeddedState()
    {
        _embeddedConfirmed = true;
        Disabled = true;
        Text = "Resize unavailable (embedded)";
        TooltipText = "The game is running in the editor's embedded game view, which cannot " +
                      "be resized. Run it as a standalone window (run_game.bat) to use this toggle.";
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

        if (_embeddedConfirmed)
        {
            SetEmbeddedState();
            return;
        }

        var isFull = IsAtSize(DisplayServer.WindowGetSize(window.GetWindowId()), FullscreenSize);
        Text = isFull ? "Windowed (1280x720)" : "Fullscreen (1920x1080)";
        TooltipText = isFull
            ? "Switch back to a 1280x720 windowed view"
            : "Switch the window to a 1920x1080 fullscreen view";
    }
}
