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
        _backdrop.AddThemeStyleboxOverride("panel", UiStyles.ModalBackdrop());
        AddChild(_backdrop);

        // Center the card with a CenterContainer so it is always fully visible and never
        // clipped at a corner (the previous center-anchor + Grow=Both pattern overflowed).
        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        center.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(center);

        var card = new PanelContainer();
        card.MouseFilter = MouseFilterEnum.Stop;
        card.AddThemeStyleboxOverride("panel", UiStyles.ModalCard());
        center.AddChild(card);

        var box = new VBoxContainer();
        box.CustomMinimumSize = new Vector2(540, 0);
        box.AddThemeConstantOverride("separation", 16);
        card.AddChild(box);

        var title = new Label { Text = "Display Settings", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 28);
        title.AddThemeColorOverride("font_color", UiStyles.TitleText);
        box.AddChild(title);

        box.AddChild(new HSeparator());

        var caption = new Label { Text = "Screen size / mode" };
        caption.AddThemeFontSizeOverride("font_size", 16);
        caption.AddThemeColorOverride("font_color", UiStyles.BodyText);
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
                Text = "Running in the editor's game preview: window size is controlled by the\neditor here. Use the Game panel's separate-window mode (or a build) to change it.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            note.AddThemeColorOverride("font_color", UiStyles.AccentText);
            box.AddChild(note);
        }

        _status = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _status.AddThemeColorOverride("font_color", UiStyles.BodyText);
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
        _status.AddThemeColorOverride("font_color", isError ? UiStyles.ErrorText : UiStyles.BodyText);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            switch (key.Keycode)
            {
                case Key.Enter:
                case Key.KpEnter:
                    OnApply();
                    GetViewport().SetInputAsHandled();
                    break;
                case Key.Escape:
                    Close();
                    GetViewport().SetInputAsHandled();
                    break;
            }
        }
    }

    private void Close()
    {
        QueueFree();
    }
}
