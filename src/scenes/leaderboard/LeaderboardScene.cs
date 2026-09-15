using System;
using System.Text.Json;
using DuelMasters.Core.Autoload;
using DuelMasters.UI.Settings;
using Godot;

namespace DuelMasters.Scenes.Leaderboard;

/// <summary>
/// Ranked ladder view over the ELO ratings computed server-side at winner
/// resolution. Layout lives in LeaderboardScene.tscn (unique-name nodes,
/// editor connections) for designer editing; this script only wires data.
/// Tapping a row fetches that player's public profile (never email/DOB) into
/// a dialog.
/// </summary>
public partial class LeaderboardScene : Control
{
	private const string ApiBase = "http://127.0.0.1:8080";
	private const string MainMenuPath = "res://src/ui/main_menu/MainMenu.tscn";

	private Label _meRank = null!;
	private ItemList _board = null!;
	private Label _status = null!;
	private AcceptDialog _publicCard = null!;
	private HttpRequest _http = null!;

	private string _token = "";
	private string _lastPath = "";

	public override void _Ready()
	{
		_meRank = GetNode<Label>("%MeRank");
		_board = GetNode<ItemList>("%BoardList");
		_status = GetNode<Label>("%StatusLabel");
		_publicCard = GetNode<AcceptDialog>("%PublicCard");
		_http = GetNode<HttpRequest>("%Http");

		_token = Global.Instance.Token;
		_board.ItemSelected += OnBoardSelected;

		AddChild(new SceneOptionsMenu { ShowBackToMenu = true });

		if (_token.Length == 0)
		{
			SetStatus("You are browsing as a guest. Sign in to view the ladder.", true);
			return;
		}
		Fire("/api/ranking/me", "GET", null);
	}

	private void OnBackPressed() => GetTree().ChangeSceneToFile(MainMenuPath);

	private void OnBoardSelected(long index)
	{
		var userId = _board.GetItemMetadata((int)index).AsString();
		if (userId.Length == 0)
			return;
		Fire("/api/profile/" + userId, "GET", null);
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
		string text = System.Text.Encoding.UTF8.GetString(body);
		if (result != (long)HttpRequest.Result.Success)
		{
			SetStatus($"Request failed. Is the server running at {ApiBase}? (result {result})", true);
			return;
		}
		if (responseCode is < 200 or >= 300)
		{
			if (responseCode == 401)
			{
				Global.Instance.Token = "";
				SessionStore.Clear();
				SetStatus("Session expired or missing. Please sign in again.", true);
				GetTree().CreateTimer(1.2).Timeout += () => GetTree().ChangeSceneToFile(MainMenuPath);
				return;
			}
			Fail(responseCode, text);
			return;
		}
		try
		{
			using var doc = JsonDocument.Parse(text);
			var root = doc.RootElement;
			var path = _lastPath;
			if (path == "GET /api/ranking/me")
			{
				FillMe(root);
				Fire("/api/ranking/leaderboard?top=50", "GET", null);
			}
			else if (path.StartsWith("GET /api/ranking/leaderboard"))
			{
				FillBoard(root);
			}
			else if (path.StartsWith("GET /api/profile/"))
			{
				FillPublicCard(root);
			}
		}
		catch (Exception)
		{
			SetStatus($"Unexpected response: {Truncate(text)}", true);
		}
	}

	private void FillMe(JsonElement root)
	{
		var nickname = Str(root, "nickname");
		var rating = Num(root, "rating");
		var rank = Num(root, "rank");
		var wins = Num(root, "onlineWins");
		var losses = Num(root, "onlineLosses");
		_meRank.Text = $"You: #{rank}  {nickname}  {rating}  ({wins}W/{losses}L)";
		SetStatus("", false);
	}

	private void FillBoard(JsonElement root)
	{
		_board.Clear();
		if (root.ValueKind != JsonValueKind.Array)
			return;
		var i = 0;
		foreach (var e in root.EnumerateArray())
		{
			i++;
			var nickname = Str(e, "nickname");
			var rating = Num(e, "rating");
			var wins = Num(e, "onlineWins");
			var losses = Num(e, "onlineLosses");
			var userId = Str(e, "userId");
			var idx = _board.AddItem($"#{i}  {nickname}  {rating}  ({wins}W/{losses}L)");
			_board.SetItemMetadata(idx, userId);
		}
		if (_board.ItemCount == 0)
			_board.AddItem("No ranked players yet.");
		SetStatus("", false);
	}

	private void FillPublicCard(JsonElement root)
	{
		var nickname = Str(root, "nickname");
		var country = Str(root, "country");
		var bio = Str(root, "bio");
		var wins = Num(root, "onlineWins");
		var losses = Num(root, "onlineLosses");
		_publicCard.Title = nickname.Length > 0 ? nickname : "Player";
		_publicCard.DialogText = $"{country}\n{bio}\nW {wins} / L {losses}";
		_publicCard.PopupCentered();
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
