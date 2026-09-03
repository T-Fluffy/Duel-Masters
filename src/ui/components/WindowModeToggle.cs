using Godot;

namespace DuelMasters.UI.Components;

/// <summary>
/// A development helper button pinned to the bottom-right of a screen that toggles
/// the OS window between windowed and fullscreen mode (both directions).
///
/// It drives the root <see cref="Window"/> node (instead of raw DisplayServer calls)
/// and tracks the intended mode locally rather than reading it back from the OS, which
/// avoids async-mode races that made the label/state disagree. When returning to a
/// windowed view it restores <see cref="WindowedSize"/> and re-centers on the current screen.
/// </summary>
[GlobalClass]
public partial class WindowModeToggle : Button
{
    /// <summary>The size used when returning from fullscreen to a windowed view.</summary>
    public static readonly Vector2I WindowedSize = new(1280, 720);

    private const float CornerMargin = 16f;

    private bool _fullscreen;

    public WindowModeToggle()
    {
        Text = "";
        TooltipText = "Toggle between a windowed view and fullscreen (dev)";
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

        // Reflect the real state on startup, whichever mode the project launched in.
        _fullscreen = DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen;
        SyncLabel();
    }

    private void OnPressed()
    {
        var window = GetWindow();
        if (window is null)
            return;

        // Toggle to the opposite of the current fullscreen state.
        _fullscreen = !_fullscreen;

        if (_fullscreen)
        {
            window.Mode = Window.ModeEnum.Fullscreen;
        }
        else
        {
            // Switch to windowed first, THEN size/position (resizing while still
            // in fullscreen is a no-op and would otherwise leave the wrong size).
            window.Mode = Window.ModeEnum.Windowed;
            window.Size = WindowedSize;
            CenterOnCurrentScreen(window);
        }

        SyncLabel();
    }

    private static void CenterOnCurrentScreen(Window window)
    {
        var screen = window.CurrentScreen;
        var usable = DisplayServer.ScreenGetUsableRect(screen);
        window.Position = usable.Position + (usable.Size - window.Size) / 2;
    }

    /// <summary>Re-sync the button label to the intended window mode.</summary>
    public void SyncLabel()
    {
        Text = _fullscreen ? "Windowed" : "Fullscreen";
        TooltipText = _fullscreen
            ? "Switch back to a 1280x720 windowed view"
            : "Switch the window to fullscreen";
    }
}
