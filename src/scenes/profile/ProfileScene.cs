using System;
using System.Text.Json;
using DuelMasters.Core.Autoload;
using DuelMasters.UI.Settings;
using Godot;

namespace DuelMasters.Scenes.Profile;

/// <summary>
/// Player identity card: view + edit the Phase-1 server profile (nickname,
/// country, avatar, bio, date of birth) plus win/loss stats and recent
/// matches. Mirrors the AuthScene/DeckBuilder HTTP pattern: HttpRequest node,
/// Bearer JWT from Global, JsonDocument parsing, camelCase contract served by
/// ProfileController (verified live by the backend smoke test).
/// </summary>
public partial class ProfileScene : Control
{
    private const string ApiBase = "http://127.0.0.1:8080";
    private const string MainMenuPath = "res://src/ui/main_menu/MainMenu.tscn";
    private const int MaxAvatarBytes = 500 * 1024;

    private Label _status = null!;
    private TextureRect _avatar = null!;
    private Label _headline = null!;
    private Label _metaView = null!;
    private Label _bioView = null!;
    private ItemList _recent = null!;
    private LineEdit _nicknameEdit = null!;
    private LineEdit _countryEdit = null!;
    private LineEdit _dobEdit = null!;
    private TextEdit _bioEdit = null!;
    private Button _avatarBtn = null!;
    private Button _saveBtn = null!;
    private FileDialog _avatarDialog = null!;
    private HttpRequest _http = null!;

    private string _token = "";
    private string _lastPath = "";
    private string _avatarBase64 = "";
    private string _favouriteDeckId = "";

    public override void _Ready()
    {
        _token = Global.Instance.Token;
        _http = new HttpRequest { Timeout = 15 };
        AddChild(_http);
        _http.RequestCompleted += OnRequestCompleted;

        _avatarDialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
        };
        // Numeric enum value (AccessMode ACCESS_FILESYSTEM = 2): the typed C#
        // accessor name varies across Godot 4.x bindings, Set is stable.
        _avatarDialog.Set("access", 2);
        _avatarDialog.AddFilter("*.png", "PNG images");
        _avatarDialog.AddFilter("*.jpg, *.jpeg", "JPEG images");
        _avatarDialog.FileSelected += OnAvatarFile;
        AddChild(_avatarDialog);

        BuildUi();

        if (_token.Length == 0)
        {
            SetStatus("You are browsing as a guest. Sign in to view and edit your profile.", true);
            return;
        }
        Refresh();
    }

    private void BuildUi()
    {
        var root = new Control();
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(root);

        var scroll = new ScrollContainer();
        scroll.SetAnchorsPreset(LayoutPreset.FullRect);
        root.AddChild(scroll);

        var center = new VBoxContainer();
        center.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        center.AddThemeConstantOverride("separation", 10);
        center.Alignment = BoxContainer.AlignmentMode.Center;
        center.CustomMinimumSize = new Vector2(420, 0);
        scroll.AddChild(center);

        var title = new Label { Text = "PLAYER PROFILE", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 40);
        center.AddChild(title);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);
        row.Alignment = BoxContainer.AlignmentMode.Center;
        center.AddChild(row);

        _avatar = new TextureRect
        {
            CustomMinimumSize = new Vector2(128, 128),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        row.AddChild(_avatar);

        var idBox = new VBoxContainer();
        idBox.AddThemeConstantOverride("separation", 4);
        row.AddChild(idBox);

        _headline = new Label { Text = "..." };
        _headline.AddThemeFontSizeOverride("font_size", 28);
        idBox.AddChild(_headline);

        _metaView = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _metaView.AddThemeColorOverride("font_color", new Color(0.7f, 0.75f, 0.85f));
        idBox.AddChild(_metaView);

        _bioView = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        center.AddChild(_bioView);

        center.AddChild(new HSeparator());

        var editTitle = new Label { Text = "Edit profile", HorizontalAlignment = HorizontalAlignment.Center };
        editTitle.AddThemeFontSizeOverride("font_size", 22);
        center.AddChild(editTitle);

        _nicknameEdit = new LineEdit { PlaceholderText = "nickname (unique, max 64)", CustomMinimumSize = new Vector2(0, 36) };
        center.AddChild(_nicknameEdit);

        _countryEdit = new LineEdit { PlaceholderText = "country", CustomMinimumSize = new Vector2(0, 36) };
        center.AddChild(_countryEdit);

        _dobEdit = new LineEdit { PlaceholderText = "date of birth (YYYY-MM-DD, private)", CustomMinimumSize = new Vector2(0, 36) };
        center.AddChild(_dobEdit);

        _bioEdit = new TextEdit { PlaceholderText = "say something about yourself (max 500)", CustomMinimumSize = new Vector2(0, 80) };
        center.AddChild(_bioEdit);

        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 12);
        btnRow.Alignment = BoxContainer.AlignmentMode.Center;
        center.AddChild(btnRow);

        _avatarBtn = new Button { Text = "Choose Avatar..." };
        _avatarBtn.Pressed += () => _avatarDialog.PopupCentered(new Vector2I(640, 480));
        btnRow.AddChild(_avatarBtn);

        _saveBtn = new Button { Text = "Save" };
        _saveBtn.Pressed += OnSave;
        btnRow.AddChild(_saveBtn);

        center.AddChild(new HSeparator());

        var statsTitle = new Label { Text = "Online record", HorizontalAlignment = HorizontalAlignment.Center };
        statsTitle.AddThemeFontSizeOverride("font_size", 22);
        center.AddChild(statsTitle);

        _recent = new ItemList { CustomMinimumSize = new Vector2(0, 140) };
        center.AddChild(_recent);

        var backBtn = new Button { Text = "Back to Menu" };
        backBtn.Pressed += () => GetTree().ChangeSceneToFile(MainMenuPath);
        center.AddChild(backBtn);

        _status = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _status.AddThemeColorOverride("font_color", new Color(0.85f, 0.92f, 1f));
        center.AddChild(_status);

        root.AddChild(new SceneOptionsMenu { ShowBackToMenu = true });
    }

    private void Refresh()
    {
        Fire("/api/profile", "GET", null);
        Fire("/api/profile/stats", "GET", null);
    }

    private void OnSave()
    {
        var nickname = _nicknameEdit.Text.Trim();
        if (nickname.Length == 0)
        {
            SetStatus("Nickname is required.", true);
            return;
        }
        var dobText = _dobEdit.Text.Trim();
        Fire("/api/profile", "PUT", JsonSerializer.Serialize(new
        {
            nickname,
            country = _countryEdit.Text.Trim(),
            avatarBase64 = _avatarBase64,
            favouriteDeckId = _favouriteDeckId.Length > 0 ? _favouriteDeckId : (string?)null,
            dateOfBirth = dobText.Length > 0 ? dobText : (string?)null,
            bio = _bioEdit.Text.Trim(),
        }));
    }

    private void OnAvatarFile(string path)
    {
        byte[] bytes;
        try
        {
            bytes = FileAccess.GetFileAsBytes(path);
        }
        catch (Exception)
        {
            SetStatus("Could not read that file.", true);
            return;
        }
        if (bytes.Length == 0 || bytes.Length > MaxAvatarBytes)
        {
            SetStatus($"Avatar must be a non-empty PNG/JPEG under {MaxAvatarBytes / 1024} KB.", true);
            return;
        }
        _avatarBase64 = Convert.ToBase64String(bytes);
        var tex = DecodeAvatar(_avatarBase64);
        if (tex is null)
        {
            _avatarBase64 = "";
            SetStatus("That file is not a readable PNG/JPEG image.", true);
            return;
        }
        _avatar.Texture = tex;
        SetStatus("Avatar staged. Press Save to upload it.", false);
    }

    private void Fire(string path, string method, string? body)
    {
        var headers = new[] { "Content-Type: application/json", $"Authorization: Bearer {_token}" };
        _lastPath = method + " " + path;
        var error = _http.Request(ApiBase + path, headers, MethodFrom(method), body ?? "");
        _saveBtn.Disabled = true;
        if (error != Error.Ok)
            SetStatus($"Request could not start (error {error}).", true);
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
        _saveBtn.Disabled = false;
        string text = System.Text.Encoding.UTF8.GetString(body);
        if (result != (long)HttpRequest.Result.Success)
        {
            SetStatus($"Request failed. Is the server running at {ApiBase}? (result {result})", true);
            return;
        }
        if (responseCode is < 200 or >= 300)
        {
            Fail(responseCode, text);
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (_lastPath == "GET /api/profile/stats")
                FillStats(root);
            else
                FillProfile(root);
        }
        catch (Exception)
        {
            SetStatus($"Unexpected response: {Truncate(text)}", true);
        }
    }

    private void FillProfile(JsonElement root)
    {
        var nickname = Str(root, "nickname");
        var email = Str(root, "email");
        var country = Str(root, "country");
        var bio = Str(root, "bio");
        var dob = Str(root, "dateOfBirth");
        var wins = Num(root, "onlineWins");
        var losses = Num(root, "onlineLosses");
        _avatarBase64 = Str(root, "avatarBase64");
        _favouriteDeckId = Str(root, "favouriteDeckId");

        _headline.Text = nickname.Length > 0 ? nickname : "(no nickname)";
        _metaView.Text = $"{country}  |  {email}  |  W {wins} / L {losses}"
            + (_favouriteDeckId.Length > 0 ? $"  |  deck {_favouriteDeckId[..8]}" : "");
        _bioView.Text = bio;
        _avatar.Texture = DecodeAvatar(_avatarBase64);

        _nicknameEdit.Text = nickname;
        _countryEdit.Text = country;
        _dobEdit.Text = dob.Length >= 10 ? dob[..10] : dob;
        _bioEdit.Text = bio;

        if (_lastPath.StartsWith("PUT "))
            SetStatus("Profile saved.", false);
        else
            SetStatus("", false);
    }

    private void FillStats(JsonElement root)
    {
        _recent.Clear();
        if (root.TryGetProperty("recent", out var recent) && recent.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in recent.EnumerateArray())
            {
                var won = r.TryGetProperty("won", out var w) && w.ValueKind == JsonValueKind.True;
                var code = Str(r, "matchCode");
                var at = Str(r, "playedAtUtc");
                _recent.AddItem($"{(won ? "WIN " : "LOSS")}  {code}  {(at.Length >= 10 ? at[..10] : at)}");
            }
        }
        if (_recent.ItemCount == 0)
            _recent.AddItem("No online matches recorded yet.");
    }

    private static Texture2D? DecodeAvatar(string base64)
    {
        if (string.IsNullOrEmpty(base64))
            return null;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            var img = new Image();
            var err = img.LoadPngFromBuffer(bytes);
            if (err != Error.Ok)
                err = img.LoadJpgFromBuffer(bytes);
            if (err != Error.Ok)
                return null;
            return ImageTexture.CreateFromImage(img);
        }
        catch
        {
            return null;
        }
    }

    private static string Str(JsonElement root, string key)
        => root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? ""
            : "";

    private static int Num(JsonElement root, string key)
        => root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetInt32()
            : 0;

    private void Fail(long code, string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
            {
                SetStatus($"Error {code}: {e.GetString()}", true);
                return;
            }
        }
        catch
        {
        }
        SetStatus($"Error {code}: {Truncate(text)}", true);
    }

    private void SetStatus(string message, bool isError)
    {
        _status.Text = message;
        _status.AddThemeColorOverride("font_color", isError ? new Color(1f, 0.6f, 0.5f) : new Color(0.85f, 0.92f, 1f));
    }

    private static string Truncate(string s, int max = 200)
        => s.Length <= max ? s : s[..max] + "...";
}
