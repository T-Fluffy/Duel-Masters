using System;
using Godot;

namespace DuelMasters.Core;

/// <summary>
/// Central display / screen-size settings service. Owns the list of supported window
/// options, applies a choice to the real OS window using the raw DisplayServer API, and
/// persists the last selection to <c>user://display_settings.cfg</c> so it survives restart.
///
/// The configured baseline in <c>project.godot</c> is a 1920x1080 windowed view. When the
/// player has saved a choice it is restored at startup via <see cref="ApplySaved"/>.
///
/// When the game runs inside the Godot editor's embedded game view there is no real OS
/// window, so the display server refuses to resize/move it. <c>Apply</c> detects this
/// (<see cref="Window.IsEmbedded"/>) and reports gracefully instead of erroring.
/// </summary>
public static class DisplaySettings
{
    /// <summary>Every screen-size option the player can pick from.</summary>
    public enum Option
    {
        /// <summary>1920x1080, exclusive fullscreen.</summary>
        Fullscreen1920,

        /// <summary>1600x900, windowed.</summary>
        Windowed1600,

        /// <summary>1280x720, windowed.</summary>
        Windowed1280,
    }

    private const string SettingsPath = "user://display_settings.cfg";
    private const string Section = "display";

    private static Option? _cached;

    /// <summary>Gets the size (in OS pixels) for each option.</summary>
    public static Vector2I SizeFor(Option option) => option switch
    {
        Option.Fullscreen1920 => new Vector2I(1920, 1080),
        Option.Windowed1600 => new Vector2I(1600, 900),
        Option.Windowed1280 => new Vector2I(1280, 720),
        _ => new Vector2I(1920, 1080),
    };

    /// <summary>Gets a human-readable label for each option.</summary>
    public static string LabelFor(Option option) => option switch
    {
        Option.Fullscreen1920 => "1920x1080 Fullscreen",
        Option.Windowed1600 => "1600x900 Windowed",
        Option.Windowed1280 => "1280x720 Windowed",
        _ => "Unknown",
    };

    /// <summary>True when running in the editor's embedded game view (cannot resize).</summary>
    public static bool IsEmbedded()
    {
        var window = Engine.GetMainLoop() is SceneTree tree ? tree.Root : null;
        return window != null && window.IsEmbedded();
    }

    /// <summary>
    /// Applies <paramref name="option"/> to the real OS window. Returns <see langword="false"/>
    /// (and logs a message) when the window is embedded and therefore cannot be resized.
    /// </summary>
    public static bool Apply(Option option)
    {
        var window = Engine.GetMainLoop() is SceneTree tree ? tree.Root : null;
        if (window is null)
            return false;

        var wid = window.GetWindowId();
        var size = SizeFor(option);

        if (window.IsEmbedded())
        {
            GD.Print($"[DisplaySettings] Cannot resize: running in the editor's embedded view. " +
                     "Run as a standalone window (run_game.bat) to change screen size.");
            return false;
        }

        var mode = option == Option.Fullscreen1920
            ? DisplayServer.WindowMode.Fullscreen
            : DisplayServer.WindowMode.Windowed;

        if (option == Option.Fullscreen1920)
        {
            DisplayServer.WindowSetMode(mode, wid);
            DisplayServer.WindowSetSize(size, wid);
        }
        else
        {
            DisplayServer.WindowSetMode(mode, wid);
            DisplayServer.WindowSetSize(size, wid);
            CenterOnCurrentScreen(wid, size);
        }

        // Keep the Godot Window node's bookkeeping aligned with the real window.
        window.Size = size;
        return true;
    }

    private static void CenterOnCurrentScreen(int windowId, Vector2I size)
    {
        var screen = DisplayServer.WindowGetCurrentScreen(windowId);
        if (screen < 0 || screen >= DisplayServer.GetScreenCount())
            screen = DisplayServer.GetPrimaryScreen();

        var usable = DisplayServer.ScreenGetUsableRect(screen);
        var pos = usable.Position + (usable.Size - size) / 2;
        DisplayServer.WindowSetPosition(pos, windowId);
    }

    /// <summary>
    /// Gets the last saved option, or (when nothing is saved) <see cref="Option.Fullscreen1920"/>
    /// as the panel's default highlighted selection. The launching window itself stays at the
    /// project baseline (windowed 1920x1080) until the player applies a choice.
    /// </summary>
    public static Option Current()
    {
        if (_cached is not null)
            return _cached.Value;

        var cfg = new ConfigFile();
        if (cfg.Load(SettingsPath) == Error.Ok)
        {
            var stored = cfg.GetValue(Section, "option", -1).AsInt32();
            if (stored >= 0 && stored < Enum.GetValues<Option>().Length)
            {
                _cached = (Option)stored;
                return _cached.Value;
            }
        }

        // Nothing saved: default to the first option in the panel. This is only the
        // highlighted selection until the player applies it; the launching window stays
        // at the project baseline (windowed 1920x1080) until then.
        _cached = Option.Fullscreen1920;
        return _cached.Value;
    }

    /// <summary>Persists <paramref name="option"/> to <c>user://</c> so it survives restart.</summary>
    public static void Save(Option option)
    {
        _cached = option;
        var cfg = new ConfigFile();
        cfg.SetValue(Section, "option", (int)option);
        var error = cfg.Save(SettingsPath);
        if (error != Error.Ok)
            GD.PushWarning($"[DisplaySettings] Could not save settings: {error}");
    }

    /// <summary>Applies the saved (or default) option at startup. No-op when embedded.</summary>
    public static void ApplySaved()
    {
        var option = Current();
        // Only restore a windowed size automatically at launch; never force-fullscreen
        // over the user unless they explicitly chose it and we are not embedded.
        if (option == Option.Fullscreen1920)
            return;

        Apply(option);
    }
}
