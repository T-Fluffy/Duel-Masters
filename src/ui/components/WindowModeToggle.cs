using Godot;

namespace DuelMasters.UI.Components;

/// <summary>
/// A development helper button pinned to the bottom-right of a screen that toggles
/// the OS window between windowed and fullscreen mode in both directions.
///
/// It reads the window's <em>actual</em> mode on every press (no cached flag, so it
/// can never get out of sync with the OS), and verifies the mode applied - falling
/// back to the raw DisplayServer call for renderer/platform setups where the node's
/// Mode property is a silent no-op. Returning to a windowed view restores
/// <see cref="WindowedSize"/> and re-centers on the current screen.
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
        _fullscreen = IsFullscreen();
        SyncLabel();
    }

    private void OnPressed()
    {
        var window = GetWindow();
        if (window is null)
            return;

        // Read the actual mode rather than blindly toggling a cached bool, so a mode
        // change that didn't take effect can never leave the button permanently stuck.
        _fullscreen = !IsFullscreen();

        if (_fullscreen)
        {
            SetFullscreen(window, true);
        }
        else
        {
            SetFullscreen(window, false);
            window.Size = WindowedSize;
            CenterOnCurrentScreen(window);
        }

        SyncLabel();
    }

    private static void SetFullscreen(Window window, bool fullscreen)
    {
        var target = fullscreen ? Window.ModeEnum.Fullscreen : Window.ModeEnum.Windowed;

        // Primary path: the node-level property.
        window.Mode = target;

        // Verify it actually applied; some setups (e.g. the gl_compatibility renderer)
        // ignore the node property, so fall back to the display-server call.
        // This is idempotent, so an extra call on an async mode change is harmless.
        if (IsFullscreen() != fullscreen)
        {
            DisplayServer.WindowSetMode(fullscreen
                ? DisplayServer.WindowMode.Fullscreen
                : DisplayServer.WindowMode.Windowed);
        }
    }

    private static bool IsFullscreen()
        => DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen;

    private static void CenterOnCurrentScreen(Window window)
    {
        var screen = window.CurrentScreen;
        if (screen < 0 || screen >= DisplayServer.GetScreenCount())
            screen = DisplayServer.GetPrimaryScreen();

        var usable = DisplayServer.ScreenGetUsableRect(screen);
        window.Position = usable.Position + (usable.Size - window.Size) / 2;
    }

    /// <summary>Re-sync the button label to the current window mode.</summary>
    public void SyncLabel()
    {
        _fullscreen = IsFullscreen();
        Text = _fullscreen ? "Windowed" : "Fullscreen";
        TooltipText = _fullscreen
            ? "Switch back to a 1280x720 windowed view"
            : "Switch the window to fullscreen";
    }
}
