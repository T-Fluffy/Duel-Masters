using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using DuelMasters.Core.Autoload;
using DuelMasters.Networking;
using DuelMasters.UI.Settings;
using Godot;

namespace DuelMasters.Scenes.NetworkLobby;

/// <summary>
/// Phase 4 lobby: connects the client to the authoritative SignalR hub and lets a
/// player host a match or join one by code. Each side may bring a saved deck (fetched
/// from <c>GET /api/decks</c> with the shared JWT); without one, the server builds a
/// random deck. The board (NetworkArena) is entered once the first authoritative
/// <see cref="DuelGameState"/> arrives (i.e. both sides are seated and the engine started).
/// </summary>
public partial class NetworkLobby : Control
{
    private const string ArenaPath = "res://src/scenes/network_arena/NetworkArena.tscn";
    private const string MainMenuPath = "res://src/ui/main_menu/MainMenu.tscn";

    private LineEdit _serverUrl = null!;
    private LineEdit _name = null!;
    private LineEdit _code = null!;
    private OptionButton _deckPicker = null!;
    private Label _status = null!;
    private Button _host = null!;
    private Button _join = null!;
    private Button _vsAi = null!;
    private bool _connecting;

    private HttpRequest _http = null!;
    private string _token = "";

    /// <summary>Deck ids aligned 1:1 with the picker items; "" means "random deck".</summary>
    private readonly List<string> _deckIds = new();

    public override void _Ready()
    {
        BuildUi();

        _token = Global.Instance.Token;
        _http = new HttpRequest { Timeout = 15 };
        AddChild(_http);
        _http.RequestCompleted += OnRequestCompleted;

        LoadDecks();
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

        var deckRow = new HBoxContainer();
        deckRow.AddThemeConstantOverride("separation", 8);
        var deckLabel = new Label { Text = "Your deck:" };
        _deckPicker = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _deckPicker.AddItem("Random deck (built from catalog)");
        _deckIds.Add("");
        deckRow.AddChild(deckLabel);
        deckRow.AddChild(_deckPicker);
        center.AddChild(deckRow);

        _host = new Button { Text = "Host Match" };
        _host.Pressed += OnHost;
        center.AddChild(_host);

        _vsAi = new Button { Text = "Practice vs AI" };
        _vsAi.Pressed += OnHostVsAi;
        center.AddChild(_vsAi);

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
            Text = "Please ensure the backend is running, then host, practice vs AI, or join.",
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
        await ConnectAndRun(() => NetworkClient.HostMatch(PlayerName(), SelectedDeckGuid()));
    }

    private async void OnHostVsAi()
    {
        await ConnectAndRun(() => NetworkClient.HostMatch(PlayerName(), SelectedDeckGuid(), vsAi: true));
    }

    private async void OnJoin()
    {
        var code = _code.Text.Trim();
        if (code.Length == 0)
        {
            SetStatus("Please enter a match code.", isError: true);
            return;
        }
        await ConnectAndRun(() => NetworkClient.JoinMatch(code, PlayerName(), SelectedDeckGuid()));
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
        _vsAi.Disabled = !enabled;
    }

    // --------------------------------------------------------------- decks

    private void LoadDecks()
    {
        if (_token.Length == 0)
        {
            SetStatus("Not logged in - you will play with a random deck. Log in from the deck builder to use a saved deck.", isError: false);
            return;
        }
        var error = _http.Request(ApiBase() + "/api/decks",
            new[] { "Content-Type: application/json", $"Authorization: Bearer {_token}" },
            HttpClient.Method.Get, "");
        if (error != Error.Ok)
            SetStatus("Could not start the decks request; you will play with a random deck.", isError: true);
    }

    private void OnRequestCompleted(long result, long responseCode, string[] headers, byte[] body)
    {
        if (result != (long)HttpRequest.Result.Success || responseCode is < 200 or >= 300)
        {
            if (!_connecting)
                SetStatus($"Could not load your decks ({responseCode}) - you will play with a random deck.", isError: true);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(body));
            var decks = doc.RootElement;
            _deckIds.Clear();
            _deckPicker.Clear();

            _deckPicker.AddItem("Random deck (built from catalog)");
            _deckIds.Add("");

            foreach (var d in decks.EnumerateArray())
            {
                var id = d.GetProperty("id").GetString() ?? "";
                var name = d.GetProperty("name").GetString() ?? "";
                _deckPicker.AddItem($"{name} ({CardsOf(d)})");
                _deckIds.Add(id);
            }

            _deckPicker.Selected = 0;
            if (decks.GetArrayLength() == 0)
                SetStatus("No saved decks yet - you will play with a random deck.", isError: false);
        }
        catch (JsonException)
        {
            SetStatus("Could not parse the decks list - you will play with a random deck.", isError: true);
        }
    }

    private static int CardsOf(JsonElement deck)
        => deck.TryGetProperty("cardCount", out var cc) && cc.TryGetInt32(out var n) ? n : 0;

    private Guid? SelectedDeckGuid()
    {
        var idx = _deckPicker.Selected;
        if (idx < 0 || idx >= _deckIds.Count)
            return null;
        var id = _deckIds[idx];
        return Guid.TryParse(id, out var guid) ? guid : null;
    }

    /// <summary>The HTTP API origin derived from the hub URL (strip the "/duel" path).</summary>
    private string ApiBase()
    {
        try
        {
            var u = new Uri(_serverUrl.Text.Trim());
            return $"{u.Scheme}://{u.Authority}";
        }
        catch (UriFormatException)
        {
            return "http://127.0.0.1:8080";
        }
    }
}