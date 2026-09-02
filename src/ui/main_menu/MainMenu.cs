using DuelMasters.UI.Components;
using Godot;

namespace DuelMasters.UI.MainMenu;

/// <summary>
/// Development launcher menu. Provides simple navigation between the two playable
/// scenes (Hotseat Arena and Deck Builder), a windowed/fullscreen toggle, and a
/// quit button.
///
/// The project's main scene is this menu, so pressing Play (F5) always lands here;
/// to run an individual scene directly use F6 (Run Current Scene).
/// </summary>
public partial class MainMenu : Control
{
    private const string ArenaPath = "res://src/scenes/arena/Arena.tscn";
    private const string DeckBuilderPath = "res://src/scenes/deck_builder/DeckBuilder.tscn";

    public override void _Ready()
    {
        BuildUi();
    }

    private void BuildUi()
    {
        var root = new Control();
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(root);

        var center = new VBoxContainer();
        center.SetAnchorsPreset(LayoutPreset.Center);
        center.GrowHorizontal = GrowDirection.Both;
        center.GrowVertical = GrowDirection.Both;
        center.AddThemeConstantOverride("separation", 20);
        center.Alignment = BoxContainer.AlignmentMode.Center;
        root.AddChild(center);

        var title = new Label
        {
            Text = "DUEL MASTERS",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 64);
        center.AddChild(title);

        var subtitle = new Label
        {
            Text = "Dev Launcher",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        subtitle.AddThemeFontSizeOverride("font_size", 24);
        subtitle.AddThemeColorOverride("font_color", new Color(0.7f, 0.75f, 0.85f));
        center.AddChild(subtitle);

        center.AddChild(new Control { CustomMinimumSize = new Vector2(0, 24) });

        var arenaBtn = new Button { Text = "Hotseat Arena" };
        arenaBtn.Pressed += () => GetTree().ChangeSceneToFile(ArenaPath);
        center.AddChild(arenaBtn);

        var deckBtn = new Button { Text = "Deck Builder" };
        deckBtn.Pressed += () => GetTree().ChangeSceneToFile(DeckBuilderPath);
        center.AddChild(deckBtn);

        center.AddChild(new Control { CustomMinimumSize = new Vector2(0, 12) });

        var hint = new Label
        {
            Text = "Dev note: Play (F5) runs this menu. F6 runs the scene you have open in the editor.",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        hint.AddThemeFontSizeOverride("font_size", 14);
        hint.AddThemeColorOverride("font_color", new Color(0.55f, 0.6f, 0.68f));
        center.AddChild(hint);

        var quitBtn = new Button { Text = "Quit" };
        quitBtn.Pressed += QuitGame;
        center.AddChild(quitBtn);

        // Fullscreen toggle pinned to the bottom-right.
        var toggle = new WindowModeToggle();
        root.AddChild(toggle);
    }

    private static void QuitGame()
    {
        // Same close pattern used by MainGame.QuitGame.
        var sceneTree = Engine.GetMainLoop() as SceneTree;
        sceneTree?.Root.PropagateNotification((int)Godot.Node.NotificationWMCloseRequest);
        sceneTree?.Quit();
    }
}
