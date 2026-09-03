using System;
using DuelMasters.Core;
using Godot;

namespace DuelMasters.UI.Settings;

/// <summary>
/// A modal "Display Settings" overlay that lets the player choose a screen size /
/// fullscreen option and apply it to the real OS window (persisted across sessions).
///
/// This panel is built in code and parented to a full-rect root so it dims the scene and
/// sits on top. It is opened from the Main Menu or from the in-scene options menu.
/// </summary>
public partial class DisplaySettingsPanel : Control
{
    private Panel _backdrop = null!;
    private OptionButton _option = null!;
    private Label _status = null!;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        BuildUi();
    }

    private void BuildUi()
    {
        _backdrop = new Panel();
        _backdrop.SetAnchorsPreset(LayoutPreset.FullRect);
        _backdrop.MouseFilter = MouseFilterEnum.Stop;
        AddChild(_backdrop);

        var card = new PanelContainer();
        card.SetAnchorsPreset(LayoutPreset.Center);
        card.GrowHorizontal = GrowDirection.Both;
        card.GrowVertical = GrowDirection.Both;
        AddChild(card);

        var box = new VBoxContainer();
        box.CustomMinimumSize = new Vector2(460, 0);
        box.AddThemeConstantOverride("separation", 16);
        card.AddChild(box);

        var title = new Label { Text = "Display Settings", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 28);
        box.AddChild(title);

        box.AddChild(new HSeparator());

        var caption = new Label { Text = "Screen size / mode" };
        caption.AddThemeFontSizeOverride("font_size", 16);
        box.AddChild(caption);

        _option = new OptionButton();
        foreach (var option in Enum.GetValues<DisplaySettings.Option>())
            _option.AddItem(DisplaySettings.LabelFor(option), (int)option);
        _option.Select((int)DisplaySettings.Current());
        box.AddChild(_option);

        if (DisplaySettings.IsEmbedded())
        {
            var note = new Label
            {
                Text = "Resize is disabled in the editor's embedded view.\nRun as a standalone window (run_game.bat) to change screen size.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            note.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.5f));
            box.AddChild(note);
        }

        _status = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _status.AddThemeColorOverride("font_color", new Color(0.85f, 0.92f, 1f));
        box.AddChild(_status);

        box.AddChild(new HSeparator());

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 12);
        buttons.Alignment = BoxContainer.AlignmentMode.Center;
        box.AddChild(buttons);

        var apply = new Button { Text = "Apply" };
        apply.Pressed += OnApply;
        buttons.AddChild(apply);

        var close = new Button { Text = "Close" };
        close.Pressed += Close;
        buttons.AddChild(close);
    }

    private void OnApply()
    {
        var option = (DisplaySettings.Option)_option.GetSelectedId();
        if (DisplaySettings.IsEmbedded())
        {
            SetStatus("Cannot resize the editor's embedded view. Run standalone first.", true);
            return;
        }

        if (DisplaySettings.Apply(option))
        {
            DisplaySettings.Save(option);
            SetStatus($"Applied: {DisplaySettings.LabelFor(option)}", false);
        }
        else
        {
            SetStatus("Could not apply the display option.", true);
        }
    }

    private void SetStatus(string message, bool isError)
    {
        _status.Text = message;
        _status.AddThemeColorOverride("font_color", isError ? new Color(1f, 0.6f, 0.5f) : new Color(0.85f, 0.92f, 1f));
    }

    private void Close()
    {
        QueueFree();
    }
}
