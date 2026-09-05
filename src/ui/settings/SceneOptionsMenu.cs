using System;
using DuelMasters.Core;
using Godot;

namespace DuelMasters.UI.Settings;

/// <summary>
/// A reusable top-right options trigger for a scene. Shows a gear glyph that opens a
/// small popup menu with "Display Settings", "Back to Main Menu" and "Exit Game".
///
/// Add an instance to any scene root (or to a Control parent) and it will anchor itself
/// to the top-right corner. The "Back to Main Menu" entry can be hidden via
/// <see cref="ShowBackToMenu"/> (e.g. where there is no menu to return to).
/// </summary>
[GlobalClass]
public partial class SceneOptionsMenu : Control
{
    private const string MainMenuPath = "res://src/ui/main_menu/MainMenu.tscn";
    private const float CornerMargin = 16f;

    /// <summary>Whether the "Back to Main Menu" entry is shown.</summary>
    public bool ShowBackToMenu { get; set; } = true;

    private Button _gear = null!;
    private PanelContainer _menu = null!;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.TopRight);
        OffsetLeft = -60f;
        OffsetTop = CornerMargin;
        OffsetRight = -CornerMargin;
        OffsetBottom = CornerMargin + 44f;
        MouseFilter = MouseFilterEnum.Stop;

        BuildGear();
    }

    private void BuildGear()
    {
        _gear = new Button { Text = "\u2699" };
        _gear.CustomMinimumSize = new Vector2(44, 44);
        _gear.TooltipText = "Options";
        _gear.Pressed += OnGearPressed;
        AddChild(_gear);
    }

    private void OnGearPressed()
    {
        // Toggle the popup menu.
        if (_menu is not null && IsInstanceValid(_menu))
        {
            _menu.QueueFree();
            _menu = null!;
            return;
        }

        _menu = new PanelContainer();
        _menu.AddThemeStyleboxOverride("panel", UiStyles.Popup());
        AddChild(_menu);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        _menu.AddChild(box);

        AddEntry(box, "Display Settings", OnDisplaySettings);
        AddCheckbox(box, "Show AI Cards", GameSettings.RevealAiHand, GameSettings.SetRevealAiHand);
        if (ShowBackToMenu)
            AddEntry(box, "Back to Main Menu", OnBackToMenu);
        AddEntry(box, "Exit Game", OnExitGame);

        // Pop the menu just below the gear, anchored to the top-right.
        _menu.SetPosition(new Vector2(-170f, 52f));
    }

    private static void AddEntry(VBoxContainer box, string text, Action onClick)
    {
        var b = new Button { Text = text };
        b.CustomMinimumSize = new Vector2(160, 0);
        b.Alignment = HorizontalAlignment.Left;
        b.Pressed += () => onClick();
        box.AddChild(b);
    }

    private static void AddCheckbox(VBoxContainer box, string text, bool initial, Action<bool> onChange)
    {
        var c = new CheckBox
        {
            Text = text,
            ButtonPressed = initial,
            CustomMinimumSize = new Vector2(160, 0),
            TooltipText = "Development aid: reveal the opponent's hand (face-up) to inspect the AI's plays.",
        };
        c.Toggled += value => onChange(value);
        box.AddChild(c);
    }

    private void OnDisplaySettings()
    {
        var panel = new DisplaySettingsPanel();
        GetTree().CurrentScene.AddChild(panel);
        CloseMenu();
    }

    private void OnBackToMenu()
    {
        CloseMenu();
        GetTree().ChangeSceneToFile(MainMenuPath);
    }

    private void OnExitGame()
    {
        CloseMenu();
        var sceneTree = Engine.GetMainLoop() as SceneTree;
        sceneTree?.Root.PropagateNotification((int)Godot.Node.NotificationWMCloseRequest);
        sceneTree?.Quit();
    }

    private void CloseMenu()
    {
        if (_menu is not null && IsInstanceValid(_menu))
        {
            _menu.QueueFree();
            _menu = null!;
        }
    }
}
