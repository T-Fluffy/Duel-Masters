using Godot;

namespace DuelMasters.UI.Components;

/// <summary>
/// A development helper button pinned to the bottom-right of a screen that toggles
/// the OS window between windowed and fullscreen mode.
///
/// On start the label reflects the real current window mode (so it works whether the
/// project launches windowed or fullscreen). When switching back to windowed the
/// window is resized to <see cref="WindowedSize"/> and centered on the current screen.
/// </summary>
[GlobalClass]
public partial class WindowModeToggle : Button
{
    /// <summary>The size used when returning from fullscreen to a windowed view.</summary>
    public static readonly Vector2I WindowedSize = new(1280, 720);

    private const float CornerMargin = 16f;

    public WindowModeToggle()
    {
        Text = "";
        TooltipText = "Toggle between a windowed view and fullscreen (dev)";
        PivotOffset = Vector2.Zero;
        SyncLabel();
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
        var name = DisplayServer.WindowGetMode();
        if (name == DisplayServer.WindowMode.Windowed ||
            name == DisplayServer.WindowMode.Minimized ||
            name == DisplayServer.WindowMode.Maximized)
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
        }
        else
        {
            DisplayServer.WindowSetSize(WindowedSize);
            // Re-center the windowed view on the screen it is currently on.
            var screen = DisplayServer.WindowGetCurrentScreen();
            var screenRect = DisplayServer.ScreenGetUsableRect(screen);
            var pos = screenRect.Position + (screenRect.Size - WindowedSize) / 2;
            DisplayServer.WindowSetPosition(pos);
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
        }
        SyncLabel();
    }

    /// <summary>Re-sync the button label to the actual window mode (call after changing mode externally).</summary>
    public void SyncLabel()
    {
        var isWindowed = DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Windowed;
        Text = isWindowed ? "Fullscreen" : "Windowed";
        TooltipText = isWindowed
            ? "Switch the window to fullscreen"
            : "Switch back to a 1280x720 windowed view";
    }
}
