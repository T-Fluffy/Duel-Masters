using System.Linq;
using DuelMasters.Networking;
using DuelMasters.UI.Settings;
using Godot;

namespace DuelMasters.Scenes.NetworkLobby;

/// <summary>
/// Phase 4 lobby: connects the client to the authoritative SignalR hub and lets a
/// player host a match or join one by code. The board (NetworkArena) is entered once
/// the first authoritative <see cref="DuelGameState"/> arrives (i.e. both sides are
/// seated and the engine has started).
/// </summary>
public partial class NetworkLobby : Control
{
    private const string ArenaPath = "res://src/scenes/network_arena/NetworkArena.tscn";
    private const string MainMenuPath = "res://src/ui/main_menu/MainMenu.tscn";

    private LineEdit _serverUrl = null!;
    private LineEdit _name = null!;
    private LineEdit _code = null!;
    private Label _status = null!;
    private Button _host = null!;
    private Button _join = null!;
    private bool _connecting;

    public override void _Ready()
    {
        BuildUi();
    }

    public override void _Process(double delta)
    {
        if (_connecting)
        {
            if (NetworkClient.TryDequeueError(out var err))
            {
                _connecting = false;
                SetStatus(err, isError: true);
                SetButtonsEnabled(true);
            }
            else if (NetworkClient.TryDequeueState(out _))
            {
                _connecting = false;
                GoToArena();
            }
        }

        // Always surface errors that arrive outside the connecting window.
        if (!_connecting && NetworkClient.TryDequeueError(out var connErr))
            SetStatus(connErr, isError: true);
    }

    // --------------------------------------------------------------- layout

    private void BuildUi()
    {
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 32);
        margin.AddThemeConstantOverride("margin_top", 32);
        margin.AddThemeConstantOverride("margin_right", 32);
        margin.AddThemeConstantOverride("margin_bottom", 32);
        AddChild(margin);

        var center = new VBoxContainer();
        center.SetAnchorsPreset(LayoutPreset.Center);
        center.GrowHorizontal = GrowDirection.Both;
        center.GrowVertical = GrowDirection.Both;
        center.AddThemeConstantOverride("separation", 14);
        center.Alignment = BoxContainer.AlignmentMode.Center;
        margin.AddChild(center);

        var title = new Label
        {
            Text = "ONLINE DUEL",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 40);
        center.AddChild(title);

        _serverUrl = new LineEdit { Text = NetworkClient.DefaultServerUrl, PlaceholderText = "Server URL" };
        center.AddChild(_serverUrl);

        _name = new LineEdit { PlaceholderText = "Your name", MaxLength = 20 };
        _name.Text = System.Environment.UserName;
        center.AddChild(_name);

        _host = new Button { Text = "Host Match" };
        _host.Pressed += OnHost;
        center.AddChild(_host);

        var joinRow = new HBoxContainer();
        joinRow.AddThemeConstantOverride("separation", 8);
        _code = new LineEdit { PlaceholderText = "Match code", MaxLength = 6 };
        _code.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        joinRow.AddChild(_code);
        _join = new Button { Text = "Join" };
        _join.Pressed += OnJoin;
        joinRow.AddChild(_join);
        center.AddChild(joinRow);

        _status = new Label
        {
            Text = "Please ensure the backend is running, then host or join.",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _status.AddThemeFontSizeOverride("font_size", 16);
        center.AddChild(_status);

        var back = new Button { Text = "Back to Main Menu" };
        back.Pressed += () => GetTree().ChangeSceneToFile(MainMenuPath);
        center.AddChild(back);

        AddChild(new SceneOptionsMenu { ShowBackToMenu = true });
    }

    // --------------------------------------------------------------- actions

    private async void OnHost()
    {
        await ConnectAndRun(() => NetworkClient.HostMatch(PlayerName()));
    }

    private async void OnJoin()
    {
        var code = _code.Text.Trim();
        if (code.Length == 0)
        {
            SetStatus("Please enter a match code.", isError: true);
            return;
        }
        await ConnectAndRun(() => NetworkClient.JoinMatch(code, PlayerName()));
    }

    private async System.Threading.Tasks.Task ConnectAndRun(System.Action afterConnect)
    {
        _connecting = true;
        SetButtonsEnabled(false);
        SetStatus("Connecting to the server...");

        try
        {
            await NetworkClient.ConnectAsync(_serverUrl.Text.Trim());
            afterConnect();
        }
        catch (System.Exception ex)
        {
            _connecting = false;
            SetButtonsEnabled(true);
            SetStatus($"Could not connect: {ex.Message}", isError: true);
        }
    }

    private string PlayerName()
    {
        var name = _name.Text.Trim();
        return name.Length == 0 ? "Player" : name;
    }

    private void GoToArena() => GetTree().ChangeSceneToFile(ArenaPath);

    private void SetStatus(string text, bool isError = false)
    {
        _status.Text = text;
        _status.Modulate = isError ? new Color(1f, 0.6f, 0.5f) : Colors.White;
    }

    private void SetButtonsEnabled(bool enabled)
    {
        _host.Disabled = !enabled;
        _join.Disabled = !enabled;
    }
}
