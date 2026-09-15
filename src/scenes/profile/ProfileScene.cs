using System;
using System.Text.Json;
using DuelMasters.Core.Autoload;
using DuelMasters.UI.Settings;
using Godot;

namespace DuelMasters.Scenes.Profile;

/// <summary>
/// Player identity card: view + edit the server profile (nickname, country,
/// avatar, bio, date of birth, background) plus win/loss stats and recent
/// matches. The whole layout lives in ProfileScene.tscn (unique-name nodes,
/// editor connections) so a designer can rearrange, style, and re-background
/// it in the Godot inspector; this script only wires data. Uses the
/// AuthScene/DeckBuilder HTTP pattern: Bearer JWT from Global, JsonDocument
/// parsing, camelCase contract served by ProfileController.
/// </summary>
public partial class ProfileScene : Control
{
    private const string ApiBase = "http://127.0.0.1:8080";
    private const string MainMenuPath = "res://src/ui/main_menu/MainMenu.tscn";
    private const string AuthPath = "res://src/scenes/auth/AuthScene.tscn";
    private const int MaxImageBytes = 500 * 1024;

    private TextureRect _background = null!;
    private TextureRect _avatar = null!;
    private Label _headline = null!;
    private Label _metaView = null!;
    private Label _bioView = null!;
    private ItemList _recent = null!;
    private LineEdit _nicknameEdit = null!;
    private LineEdit _countryEdit = null!;
    private LineEdit _dobEdit = null!;
    private TextEdit _bioEdit = null!;
    private Button _saveBtn = null!;
    private Label _status = null!;
    private FileDialog _imagePicker = null!;
    private HttpRequest _http = null!;

    private string _token = "";
    private string _lastPath = "";
    private string _avatarBase64 = "";
    private string _backgroundBase64 = "";
    private string _favouriteDeckId = "";
    private bool _pickingBackground;

    public override void _Ready()
    {
        _background = GetNode<TextureRect>("%Background");
        _avatar = GetNode<TextureRect>("%Avatar");
        _headline = GetNode<Label>("%Headline");
        _metaView = GetNode<Label>("%Meta");
        _bioView = GetNode<Label>("%BioView");
        _recent = GetNode<ItemList>("%RecentList");
        _nicknameEdit = GetNode<LineEdit>("%NicknameEdit");
        _countryEdit = GetNode<LineEdit>("%CountryEdit");
        _dobEdit = GetNode<LineEdit>("%DobEdit");
        _bioEdit = GetNode<TextEdit>("%BioEdit");
        _saveBtn = GetNode<Button>("%SaveBtn");
        _status = GetNode<Label>("%StatusLabel");
        _imagePicker = GetNode<FileDialog>("%ImagePicker");
        _http = GetNode<HttpRequest>("%Http");

        _token = Global.Instance.Token;

        AddChild(new SceneOptionsMenu { ShowBackToMenu = true });

        if (_token.Length == 0)
        {
            SetStatus("You are browsing as a guest. Sign in to view and edit your profile.", true);
            return;
        }
        Refresh();
    }

    private void Refresh()
    {
        // One request at a time: stats are chained after the profile reply
        // (see OnRequestCompleted). Firing both at once overwrites _lastPath
        // and collides on the single HttpRequest node.
        Fire("/api/profile", "GET", null);
    }

    private void OnAvatarPressed()
    {
        _pickingBackground = false;
        _imagePicker.PopupCentered(new Vector2I(640, 480));
    }

    private void OnBackgroundPressed()
    {
        _pickingBackground = true;
        _imagePicker.PopupCentered(new Vector2I(640, 480));
    }

    private void OnBackPressed() => GetTree().ChangeSceneToFile(MainMenuPath);

    private void OnSavePressed() => OnSave();

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
            backgroundBase64 = _backgroundBase64,
        }));
    }

    private void OnImageFile(string path)
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
        if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
        {
            SetStatus($"Image must be a non-empty PNG/JPEG under {MaxImageBytes / 1024} KB.", true);
            return;
        }
        var staged = Convert.ToBase64String(bytes);
        var tex = DecodeImage(staged);
        if (tex is null)
        {
            SetStatus("That file is not a readable PNG/JPEG image.", true);
            return;
        }
        if (_pickingBackground)
        {
            _backgroundBase64 = staged;
            _background.Texture = tex;
            SetStatus("Background staged. Press Save to upload it.", false);
        }
        else
        {
            _avatarBase64 = staged;
            _avatar.Texture = tex;
            SetStatus("Avatar staged. Press Save to upload it.", false);
        }
    }

    private void Fire(string path, string method, string? body)
    {
        var headers = new[] { "Content-Type: application/json", $"Authorization: Bearer {_token}" };
        if (_http.GetHttpClientStatus() != HttpClient.Status.Disconnected)
        {
            SetStatus("Still talking to the server, try again in a moment.", true);
            return;
        }
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
            var path = _lastPath;
            if (path == "GET /api/profile/stats")
            {
                FillStats(root);
            }
            else
            {
                FillProfile(root);
                if (path == "GET /api/profile")
                    Fire("/api/profile/stats", "GET", null);
            }
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
        _backgroundBase64 = Str(root, "backgroundBase64");
        _favouriteDeckId = Str(root, "favouriteDeckId");

        _headline.Text = nickname.Length > 0 ? nickname : "(no nickname)";
        _metaView.Text = $"{country}  |  {email}  |  W {wins} / L {losses}"
            + (_favouriteDeckId.Length > 0 ? $"  |  deck {_favouriteDeckId[..8]}" : "");
        _bioView.Text = bio;
        _avatar.Texture = DecodeImage(_avatarBase64);
        _background.Texture = DecodeImage(_backgroundBase64);

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

    private static Texture2D? DecodeImage(string base64)
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
        // Any 401 means the stored token is missing, expired, or foreign:
        // drop the dead session and send the player back to sign in instead
        // of leaving a bare error code on screen.
        if (code == 401)
        {
            Global.Instance.Token = "";
            SessionStore.Clear();
            SetStatus("Session expired or missing. Please sign in again.", true);
            GetTree().CreateTimer(1.2).Timeout += () => GetTree().ChangeSceneToFile(AuthPath);
            return;
        }
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
