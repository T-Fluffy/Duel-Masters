using System;
using System.Collections.Generic;
using System.Text.Json;
using DuelMasters.Core.Autoload;
using DuelMasters.UI.Components;
using Godot;

namespace DuelMasters.Scenes.Auth;

/// <summary>
/// Login / Register gate shown before anything else. This is the project's main
/// scene so the player authenticates with the Phase 1.5 .NET backend (JWT) before
/// they can reach the main menu. On a successful login/register the session is
/// stored on the Global autoload and the scene advances to the main menu.
///
/// If the backend is unreachable the player can still proceed to the menu as a
/// guest (an explicit "Continue as Guest" path keeps the app usable offline).
/// </summary>
public partial class AuthScene : Control
{
    private const string ApiBase = "http://127.0.0.1:8080";
    private const string MainMenuPath = "res://src/ui/main_menu/MainMenu.tscn";

    private LineEdit _userEdit = null!;
    private LineEdit _emailEdit = null!;
    private LineEdit _passEdit = null!;
    private Button _loginBtn = null!;
    private Button _registerBtn = null!;
    private Label _status = null!;
    private HttpRequest _http = null!;

    public override void _Ready()
    {
        _http = new HttpRequest { Timeout = 15 };
        AddChild(_http);
        _http.RequestCompleted += OnRequestCompleted;

        // If already authenticated (e.g. returning to this scene), skip ahead.
        if (Global.Instance.IsAuthenticated)
        {
            GetTree().ChangeSceneToFile(MainMenuPath);
            return;
        }

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
        center.AddThemeConstantOverride("separation", 14);
        center.Alignment = BoxContainer.AlignmentMode.Center;
        center.CustomMinimumSize = new Vector2(340, 0);
        root.AddChild(center);

        var title = new Label
        {
            Text = "DUEL MASTERS",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 56);
        center.AddChild(title);

        var subtitle = new Label
        {
            Text = "Sign in to continue",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        subtitle.AddThemeFontSizeOverride("font_size", 18);
        subtitle.AddThemeColorOverride("font_color", new Color(0.7f, 0.75f, 0.85f));
        center.AddChild(subtitle);

        center.AddChild(new Control { CustomMinimumSize = new Vector2(0, 18) });

        _userEdit = new LineEdit { PlaceholderText = "username", CustomMinimumSize = new Vector2(0, 40) };
        center.AddChild(_userEdit);

        _emailEdit = new LineEdit { PlaceholderText = "email (required to register)", CustomMinimumSize = new Vector2(0, 40) };
        center.AddChild(_emailEdit);

        _passEdit = new LineEdit { PlaceholderText = "password", Secret = true, CustomMinimumSize = new Vector2(0, 40) };
        center.AddChild(_passEdit);

        center.AddChild(new Control { CustomMinimumSize = new Vector2(0, 12) });

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 12);
        buttons.Alignment = BoxContainer.AlignmentMode.Center;
        center.AddChild(buttons);

        _loginBtn = new Button { Text = "Login" };
        _loginBtn.Pressed += OnLogin;
        buttons.AddChild(_loginBtn);

        _registerBtn = new Button { Text = "Register" };
        _registerBtn.Pressed += OnRegister;
        buttons.AddChild(_registerBtn);

        center.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });

        var guestBtn = new Button { Text = "Continue as Guest" };
        guestBtn.Pressed += () => GetTree().ChangeSceneToFile(MainMenuPath);
        center.AddChild(guestBtn);

        _status = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _status.AddThemeColorOverride("font_color", new Color(0.85f, 0.92f, 1f));
        center.AddChild(_status);

        // Window-mode toggle pinned to the bottom-right corner.
        root.AddChild(new WindowModeToggle());
    }

    private void OnLogin()
    {
        var username = _userEdit.Text.Trim();
        var password = _passEdit.Text;
        if (username.Length == 0 || password.Length == 0)
        {
            SetStatus("Enter a username and password.", true);
            return;
        }
        Fire("/api/auth/login", "POST", JsonSerializer.Serialize(new { username, password }));
    }

    private void OnRegister()
    {
        var username = _userEdit.Text.Trim();
        var email = _emailEdit.Text.Trim();
        var password = _passEdit.Text;
        if (username.Length == 0 || email.Length == 0 || password.Length == 0)
        {
            SetStatus("Fill in username, email, and password to register.", true);
            return;
        }
        Fire("/api/auth/register", "POST", JsonSerializer.Serialize(new { username, email, password }));
    }

    private void Fire(string path, string method, string body)
    {
        var headers = new[] { "Content-Type: application/json" };
        var error = _http.Request(ApiBase + path, headers, MethodFrom(method), body);
        _loginBtn.Disabled = true;
        _registerBtn.Disabled = true;
        SetStatus(error == Error.Ok ? $"Sending {method} {path}..." : $"Request could not start (error {error}).", error != Error.Ok);
    }

    private static HttpClient.Method MethodFrom(string method) => method switch
    {
        "POST" => HttpClient.Method.Post,
        "PUT" => HttpClient.Method.Put,
        "DELETE" => HttpClient.Method.Delete,
        _ => HttpClient.Method.Get,
    };

    private void OnRequestCompleted(long result, long responseCode, string[] headers, byte[] body)
    {
        _loginBtn.Disabled = false;
        _registerBtn.Disabled = false;
        string text = System.Text.Encoding.UTF8.GetString(body);
        if (result != (long)HttpRequest.Result.Success)
        {
            SetStatus($"Request failed. Is the server running at {ApiBase}? (result {result})", true);
            return;
        }

        if (responseCode is < 200 or >= 300)
        {
            SetStatus($"Error {responseCode}: {Truncate(text)}", true);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var token = root.GetProperty("token").GetString() ?? "";
            var username = root.GetProperty("username").GetString() ?? _userEdit.Text.Trim();

            var global = Global.Instance;
            global.Token = token;
            global.Username = username;

            SetStatus($"Welcome, {username}! Entering the main menu...", false);
            GetTree().CreateTimer(0.6).Timeout += () => GetTree().ChangeSceneToFile(MainMenuPath);
        }
        catch (Exception)
        {
            SetStatus($"Unexpected response: {Truncate(text)}", true);
        }
    }

    private void SetStatus(string message, bool isError)
    {
        _status.Text = message;
        _status.AddThemeColorOverride("font_color", isError ? new Color(1f, 0.6f, 0.5f) : new Color(0.85f, 0.92f, 1f));
    }

    private static string Truncate(string s, int max = 200)
        => s.Length <= max ? s : s[..max] + "...";
}
