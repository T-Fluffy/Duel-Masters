using Godot;

namespace DuelMasters.Debug;

/// <summary>
/// On-screen dev overlay showing FPS and build version. Wire the two exported
/// labels in your scene to use it.
/// </summary>
[GlobalClass]
public partial class DebugOverlay : Control
{
    [Export] private Label? _fpsLabel;
    [Export] private Label? _versionInfo;

    private double _fpsTimer;

    public override void _Ready()
    {
        Variant versionVariant = ProjectSettings.GetSetting("application/config/version");
        string version = versionVariant.VariantType != Variant.Type.Nil
            ? versionVariant.AsString()
            : "?";
        if (_versionInfo != null)
            _versionInfo.Text = $"v{version}";
    }

    public override void _Process(double delta)
    {
        _fpsTimer += delta;
        if (_fpsTimer >= 0.25 && _fpsLabel != null)
        {
            _fpsLabel.Text = $"FPS: {Engine.GetFramesPerSecond():F0}";
            _fpsTimer = 0.0;
        }
    }
}