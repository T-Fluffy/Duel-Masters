using System.Collections.Generic;
using Godot;

namespace DuelMasters.Tools.Editor;

/// <summary>
/// Editor-only helper that applies the card-game baseline project settings.
/// Run it from the Godot editor: Script > Tools > Run (or File > Run while the script is open).
/// </summary>
[Tool]
public partial class ApplyProjectSettings : EditorScript
{
    private static readonly Dictionary<string, Variant> ProjectSettingsDict = new()
    {
        ["display/window/size/viewport_width"] = 1920,
        ["display/window/size/viewport_height"] = 1080,
        ["display/window/stretch/mode"] = "canvas_items",
        ["display/window/stretch/aspect"] = "keep",
        ["application/config/version"] = "0.1.0",
    };

    private static readonly Dictionary<int, string> Physics2DLayers = new()
    {
        [1] = "Cards",
        [2] = "Board",
    };

    private static readonly Dictionary<int, string> Render2DLayers = new()
    {
        [1] = "Cards",
        [2] = "Board",
        [3] = "VFX",
    };

    public override void _Run()
    {
        ApplyBaselineSettings();
        ApplyLayerNames();

        Error error = ProjectSettings.Save();

        if (error == Error.Ok)
            GD.Print("Project setup settings saved.");
        else
            GD.PushError($"Project settings could not be saved. Error: {error}");
    }

    private void ApplyBaselineSettings()
    {
        foreach (KeyValuePair<string, Variant> entry in ProjectSettingsDict)
            ProjectSettings.SetSetting(entry.Key, entry.Value);
    }

    private void ApplyLayerNames()
    {
        foreach (KeyValuePair<int, string> layer in Physics2DLayers)
            ProjectSettings.SetSetting($"layer_names/2d_physics/layer_{layer.Key}", layer.Value);

        foreach (KeyValuePair<int, string> layer in Render2DLayers)
            ProjectSettings.SetSetting($"layer_names/2d_render/layer_{layer.Key}", layer.Value);
    }
}