using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DuelMasters.Core.Autoload;
using DuelMasters.Resources;
using DuelMasters.UI;
using DuelMasters.UI.Settings;
using Godot;
using CardCatalogFile = DuelMasters.Resources.CardCatalog;

namespace DuelMasters.Scenes.DeckBuilder;

/// <summary>
/// Deck Builder scene. Loads the Phase 1 card catalog (cards.json) and lets the
/// player assemble a deck under the deck rules:
///   - exactly 40 cards
///   - at most 4 copies of any single card
/// Deck persistence talks to the Phase 1.5 .NET backend (JWT auth) over HTTP.
/// If the server is unreachable, save/load report the failure without crashing,
/// so the scene stays usable offline for browsing and deck assembly.
///
/// The session token is shared via the Global autoload (set from the login scene);
/// the inline login bar remains as a fallback for changing account.
/// </summary>
public partial class DeckBuilder : Control
{
	private const int MinCards = 40;
	private const int MaxCards = 40;
	private const int MaxCopies = 4;
	private const string CardsJsonPath = "res://src/resources/data/cards.json";
	private const string ApiBase = "http://127.0.0.1:8080";
	private const string MainMenuPath = "res://src/ui/main_menu/MainMenu.tscn";

	private readonly List<CardData> _catalog = new();
	private readonly Dictionary<string, int> _deck = new();
	private readonly Dictionary<string, CardCatalogFile.CardRecord> _parseLookup = new();

	private DuelMasters.Gameplay.CardView.CardView _preview = null!;
	private Control _previewCard = null!;

	private VBoxContainer _poolBox = null!;
	private VBoxContainer _deckList = null!;
	private Control _previewHost = null!;
	private Label _previewName = null!;
	private Label _previewInfo = null!;
	private Label _summary = null!;
	private Label _status = null!;
	private Label _authLabel = null!;
	private LineEdit _userEdit = null!;
	private LineEdit _passEdit = null!;
	private HttpRequest _http = null!;
	private string _token = "";
	private string _lastPath = "";
	private string _lastMethod = "";
	private string _selectedDeckId = "";

	private readonly List<StarterDeck> _starterDecks = new();
	private OptionButton _starterPicker = null!;
	private Label _starterDesc = null!;

	private sealed class CardData
	{
		public string Id { get; init; } = "";
		public string Name { get; init; } = "";
		public string? Civilization { get; init; }
		public int? Power { get; init; }
		public int ManaCost { get; init; }
		public string Race { get; init; } = "-";
		public string CardType { get; init; } = "Creature";
		public string ImagePath { get; init; } = "";
		public string Tooltip { get; init; } = "";
	}

	private sealed class CardJson
	{
		public string Id { get; set; } = "";
		public string? Name { get; set; }
		public string? Civilization { get; set; }
		public string CardType { get; set; } = "Creature";
		public int ManaCost { get; set; }
		public int? Power { get; set; }
		public string? Race { get; set; }
		public string ImagePath { get; set; } = "";
	}

	public override void _Ready()
	{
		_http = new HttpRequest { Timeout = 15 };
		AddChild(_http);
		_http.RequestCompleted += OnRequestCompleted;

		// Adopt the session established by the login scene, if any.
		_token = Global.Instance.Token;

		BuildUi();
		LoadCatalog();
		LoadStarterDecks();
		RefreshDeckView();
	}

	// ------------------------------------------------------------------ UI --

	private void BuildUi()
	{
		var margin = new MarginContainer();
		margin.SetAnchorsPreset(LayoutPreset.FullRect);
		margin.AddThemeConstantOverride("margin_left", 16);
		margin.AddThemeConstantOverride("margin_top", 16);
		margin.AddThemeConstantOverride("margin_right", 16);
		margin.AddThemeConstantOverride("margin_bottom", 16);
		AddChild(margin);

		var root = new VBoxContainer();
		margin.AddChild(root);

		// Header row.
		var header = new HBoxContainer();
		root.AddChild(header);

		var menuBtn = new Button { Text = "< Main Menu" };
		menuBtn.Pressed += OnBackToMenu;
		header.AddChild(menuBtn);

		header.AddChild(new Control { CustomMinimumSize = new Vector2(12, 0) });

		var title = new Label { Text = "Deck Builder" };
		title.AddThemeFontSizeOverride("font_size", 28);
		header.AddChild(title);

		header.AddChild(new Control { CustomMinimumSize = new Vector2(24, 0) });

		_userEdit = new LineEdit { PlaceholderText = "username", CustomMinimumSize = new Vector2(160, 0), Text = Global.Instance.Username };
		header.AddChild(_userEdit);
		_passEdit = new LineEdit { PlaceholderText = "password", Secret = true, CustomMinimumSize = new Vector2(160, 0) };
		header.AddChild(_passEdit);

		var loginBtn = new Button { Text = "Login" };
		loginBtn.Pressed += OnLogin;
		header.AddChild(loginBtn);

		var registerBtn = new Button { Text = "Register" };
		registerBtn.Pressed += OnRegister;
		header.AddChild(registerBtn);

		header.AddChild(new Control { CustomMinimumSize = new Vector2(16, 0) });
		_authLabel = new Label { Text = _token.Length > 0 ? $"Logged in as {Global.Instance.Username}" : "Not logged in", CustomMinimumSize = new Vector2(220, 0) };
		header.AddChild(_authLabel);

		// Body: card pool (left) + card preview (right).
		var body = new HBoxContainer { CustomMinimumSize = new Vector2(0, 560) };
		root.AddChild(body);

		body.AddChild(BuildCardPool());
		body.AddChild(BuildPreviewPanel());

		// Deck list, centered below the card pool.
		root.AddChild(BuildDeckPanel());

		_status = new Label
		{
			Text = "",
			CustomMinimumSize = new Vector2(0, 44),
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			VerticalAlignment = VerticalAlignment.Center,
		};
		root.AddChild(_status);

		// Top-right options gear (Display Settings / Back to Main Menu / Exit Game).
		AddChild(new SceneOptionsMenu { ShowBackToMenu = true });
	}

	private Control BuildCardPool()
	{
		var pool = new VBoxContainer { CustomMinimumSize = new Vector2(560, 0) };
		pool.SizeFlagsHorizontal = SizeFlags.ExpandFill;

		var label = new Label { Text = "Card Pool (DM-01..09)" };
		label.AddThemeFontSizeOverride("font_size", 20);
		pool.AddChild(label);

		var search = new LineEdit { PlaceholderText = "Search cards...", SizeFlagsHorizontal = SizeFlags.ExpandFill };
		search.TextChanged += t => { _searchQuery = t; RefreshPool(); };
		pool.AddChild(search);

		var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
		pool.AddChild(scroll);

		_poolBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		scroll.AddChild(_poolBox);
		return pool;
	}

	private Control BuildPreviewPanel()
	{
		var right = new VBoxContainer { CustomMinimumSize = new Vector2(376, 0) };
		right.Alignment = BoxContainer.AlignmentMode.Center;
		right.AddThemeConstantOverride("separation", 6);

		var title = new Label { Text = "Card Preview", HorizontalAlignment = HorizontalAlignment.Center };
		title.AddThemeFontSizeOverride("font_size", 20);
		right.AddChild(title);

		_previewHost = new CenterContainer { CustomMinimumSize = new Vector2(360, 470) };
		right.AddChild(_previewHost);

		_previewName = new Label { Text = "Click a card in the pool", HorizontalAlignment = HorizontalAlignment.Center };
		_previewName.AddThemeFontSizeOverride("font_size", 16);
		right.AddChild(_previewName);

		_previewInfo = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
		_previewInfo.AddThemeFontSizeOverride("font_size", 14);
		_previewInfo.AddThemeColorOverride("font_color", new Color(0.7f, 0.75f, 0.85f));
		right.AddChild(_previewInfo);
		return right;
	}

	private Control BuildDeckPanel()
	{
		var band = new VBoxContainer { CustomMinimumSize = new Vector2(0, 300) };
		band.Alignment = BoxContainer.AlignmentMode.Center;

		var title = new Label { Text = "Your Deck", HorizontalAlignment = HorizontalAlignment.Center };
		title.AddThemeFontSizeOverride("font_size", 20);
		band.AddChild(title);

		var starterRow = new HBoxContainer();
		starterRow.Alignment = BoxContainer.AlignmentMode.Center;
		starterRow.AddThemeConstantOverride("separation", 10);
		band.AddChild(starterRow);

		var starterLabel = new Label { Text = "Starter Decks:" };
		starterLabel.AddThemeColorOverride("font_color", UiStyles.BodyText);
		starterRow.AddChild(starterLabel);

		_starterPicker = new OptionButton { CustomMinimumSize = new Vector2(280, 0) };
		_starterPicker.AddItem("-- no starter --", 0);
		_starterPicker.ItemSelected += OnStarterSelected;
		starterRow.AddChild(_starterPicker);

		_starterDesc = new Label
		{
			Text = "",
			HorizontalAlignment = HorizontalAlignment.Center,
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			CustomMinimumSize = new Vector2(480, 0),
		};
		_starterDesc.AddThemeColorOverride("font_color", UiStyles.MutedText);
		band.AddChild(_starterDesc);

		_summary = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
		band.AddChild(_summary);

		var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(0, 180) };
		band.AddChild(scroll);

		var listWrap = new MarginContainer();
		listWrap.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
		scroll.AddChild(listWrap);

		_deckList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(700, 0) };
		listWrap.AddChild(_deckList);

		var buttons = new HBoxContainer();
		buttons.Alignment = BoxContainer.AlignmentMode.Center;
		buttons.AddThemeConstantOverride("separation", 12);
		band.AddChild(buttons);

		var saveBtn = new Button { Text = "Save Deck" };
		saveBtn.Pressed += OnSaveDeck;
		buttons.AddChild(saveBtn);

		var loadBtn = new Button { Text = "Load Decks" };
		loadBtn.Pressed += OnLoadDecks;
		buttons.AddChild(loadBtn);
		return band;
	}

	// ------------------------------------------------------------- catalog --

	private void LoadStarterDecks()
	{
		try
		{
			_starterDecks.Clear();
			_starterDecks.AddRange(StarterDecks.LoadAll());

			// ItemSelected emits the 0-based combo index; item 0 is the "no
			// starter" placeholder, so deck indices line up after it.
			for (var i = 0; i < _starterDecks.Count; i++)
				_starterPicker.AddItem(_starterDecks[i].Name, i + 1);
			_starterPicker.Disabled = _starterDecks.Count == 0;
		}
		catch (Exception ex)
		{
			SetStatus($"Could not load starter deck registry: {ex.Message}", true);
		}
	}

	private void OnStarterSelected(long index)
	{
		var slot = (int)index - 1; // index 0 is the placeholder row.
		if (slot < 0 || slot >= _starterDecks.Count)
			return;

		var deck = _starterDecks[slot];
		_deck.Clear();
		foreach (var entry in deck.Cards)
		{
			var card = _catalog.FirstOrDefault(c =>
				string.Equals(c.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
			if (card is not null)
				_deck[card.Id] = entry.Count;
		}

		_starterDesc.Text = deck.Tagline;
		RefreshDeckView();
		SetStatus($"Loaded starter deck '{deck.Name}' ({CardCount()} cards).", false);
	}

	private void LoadCatalog()
	{
		if (!FileAccess.FileExists(CardsJsonPath))
		{
			SetStatus($"Card catalog not found at {CardsJsonPath}.", true);
			return;
		}

		using var file = FileAccess.Open(CardsJsonPath, FileAccess.ModeFlags.Read);
		var text = file.GetAsText();
		var list = JsonSerializer.Deserialize<List<CardJson>>(text, new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true,
		}) ?? new List<CardJson>();

		_catalog.Clear();
		foreach (var c in list)
		{
			if (string.IsNullOrEmpty(c.Name))
				continue; // DMR-23-Promo skeleton cards have no name; keep the pool clean.
			_catalog.Add(new CardData
			{
				Id = c.Id,
				Name = c.Name!,
				Civilization = c.Civilization,
				Power = c.Power,
				ManaCost = c.ManaCost,
				Race = c.Race ?? "-",
				CardType = c.CardType,
				Tooltip = $"Power: {(c.Power?.ToString() ?? "-")}\nRace: {c.Race ?? "-"}\nType: {c.CardType}",
			});
		}
		_catalog.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

		// Build a domain-card + art lookup (via the shared catalog loader) for previews.
		_parseLookup.Clear();
		foreach (var rec in CardCatalogFile.Load())
			_parseLookup[rec.Card.Id] = rec;

		RefreshPool();
		SetStatus($"Loaded {_catalog.Count} named cards from the catalog.", false);
	}

	// ---------------------------------------------------------------- deck --

	private string _searchQuery = "";

	private void RefreshPool()
	{
		foreach (var child in _poolBox.GetChildren().OfType<Control>().ToList())
			child.QueueFree();

		var q = _searchQuery.Trim().ToLowerInvariant();
		var seen = 0;
		var total = CardCount();
		foreach (var card in _catalog)
		{
			if (q.Length > 0 && !card.Name.ToLowerInvariant().Contains(q))
				continue;

			var row = new HBoxContainer();
			var have = _deck.TryGetValue(card.Id, out var ct) ? ct : 0;

			var label = new Label
			{
				Text = $"{card.Name}  [{card.Civilization}]  {card.ManaCost} mana{(card.CardType == "Spell" ? "" : $"  {card.Power}")}",
				SizeFlagsHorizontal = SizeFlags.ExpandFill,
				TooltipText = card.Tooltip,
			};
			row.AddChild(label);

			var minus = new Button { Text = "-", Disabled = have <= 0 };
			minus.Pressed += () => RemoveCard(card.Id);
			row.AddChild(minus);

			var plus = new Button { Text = "+", Disabled = have >= MaxCopies || total >= MaxCards };
			plus.Pressed += () => AddCard(card.Id);
			row.AddChild(plus);

			var count = new Label { Text = have > 0 ? $"x{have}" : "" , CustomMinimumSize = new Vector2(28, 0) };
			row.AddChild(count);

			var preview = new Button { Text = "Preview" };
			preview.Pressed += () => ShowPreview(card);
			row.AddChild(preview);

			_poolBox.AddChild(row);
			seen++;
		}

		if (seen == 0)
		{
			var empty = new Label { Text = "No matching cards." };
			_poolBox.AddChild(empty);
		}
	}

	private void RefreshDeckView()
	{
		foreach (var child in _deckList.GetChildren().OfType<Control>().ToList())
			child.QueueFree();

		var total = CardCount();
		var sorted = _deck
			.Select(kv => (kv.Key, kv.Value))
			.OrderBy(x => _catalog.FirstOrDefault(c => c.Id == x.Key)?.Name ?? x.Key);

		foreach (var (cardId, count) in sorted)
		{
			var card = _catalog.FirstOrDefault(c => c.Id == cardId);
			var row = new HBoxContainer();
			var name = new Label
			{
				Text = $"{card?.Name ?? cardId}  x{count}",
				SizeFlagsHorizontal = SizeFlags.ExpandFill,
			};
			row.AddChild(name);

			var minus = new Button { Text = "-" };
			minus.Pressed += () => RemoveCard(cardId);
			row.AddChild(minus);

			var plus = new Button { Text = "+", Disabled = count >= MaxCopies || total >= MaxCards };
			plus.Pressed += () => AddCard(cardId);
			row.AddChild(plus);

			_deckList.AddChild(row);
		}
		RefreshPool();
		_summary.Text = total == MaxCards
			? "Ready: exactly 40 cards."
			: $"{total}/{MaxCards} cards (need {MaxCards - total} more).";
	}

	private void AddCard(string cardId)
	{
		if (CardCount() >= MaxCards)
		{
			SetStatus($"A deck can hold at most {MaxCards} cards.", true);
			return;
		}
		if (_deck.TryGetValue(cardId, out var ct) && ct >= MaxCopies)
		{
			SetStatus($"Cannot add more than {MaxCopies} copies of a card.", true);
			return;
		}
		_deck[cardId] = ct + 1;
		RefreshDeckView();
	}

	private void RemoveCard(string cardId)
	{
		if (_deck.TryGetValue(cardId, out var ct))
		{
			if (ct <= 1)
				_deck.Remove(cardId);
			else
				_deck[cardId] = ct - 1;
		}
		RefreshDeckView();
	}

	private int CardCount() => _deck.Values.Sum();

	private void SetStatus(string message, bool isError)
	{
		_status.Text = message;
		_status.Modulate = isError ? new Color(1f, 0.6f, 0.5f) : new Color(0.85f, 0.92f, 1f);
	}

	private void ShowPreview(CardData data)
	{
		ClearPreviewCard();

		_previewName.Text = data.Name;

		var civ = data.Civilization ?? "-";
		var power = data.CardType == "Spell" ? "-" : data.Power?.ToString() ?? "-";
		_previewInfo.Text = $"{civ}  •  {data.ManaCost} mana  •  {data.CardType}\nRace: {data.Race}  •  Power: {power}";

		if (!_parseLookup.TryGetValue(data.Id, out var rec))
			return;

		// Prefer the full card scan (contains all the card's text) so the player can
		// read name, costs, effect and power at a glance.
		var texture = LoadTexture(rec.ImagePath);
		if (texture is not null)
		{
			var img = new TextureRect
			{
				Texture = texture,
				ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
				StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
				CustomMinimumSize = new Vector2(340, 460),
			};
			img.SetAnchorsPreset(LayoutPreset.FullRect);
			_previewCard = img;
			_previewHost.AddChild(img);
			return;
		}

		// Fallback: procedural civilization-colored card if no artwork exists.
		_previewHost.AddChild(_preview = new DuelMasters.Gameplay.CardView.CardView(rec.Card, null));
		_preview.MouseFilter = MouseFilterEnum.Ignore;
		_preview.SetProcess(false);
	}

	private static Texture2D? LoadTexture(string? path)
	{
		if (string.IsNullOrEmpty(path) || !ResourceLoader.Exists(path))
			return null;
		try
		{
			return ResourceLoader.Load<Texture2D>(path);
		}
		catch (Exception)
		{
			return null;
		}
	}

	private void ClearPreviewCard()
	{
		if (_previewCard is not null && _previewCard.IsInsideTree())
		{
			_previewCard.QueueFree();
			_previewCard = null!;
		}
		if (_preview is not null && _preview.IsInsideTree())
		{
			_preview.QueueFree();
			_preview = null!;
		}
	}

	private void OnBackToMenu() => GetTree().ChangeSceneToFile(MainMenuPath);

	private void OnSaveDeck()
	{
		if (_token.Length == 0)
		{
			SetStatus("Log in before saving a deck.", true);
			return;
		}
		if (CardCount() != MaxCards)
		{
			SetStatus($"Deck must contain exactly {MaxCards} cards before saving.", true);
			return;
		}
		var lines = _deck.Select(kv => new { cardId = kv.Key, count = kv.Value });
		var payload = JsonSerializer.Serialize(new { name = "My Deck", cards = lines });
		Fire(_selectedDeckId.Length > 0 ? $"/api/decks/{_selectedDeckId}" : "/api/decks",
			_selectedDeckId.Length > 0 ? "PUT" : "POST", payload, auth: true);
	}

	private void OnLoadDecks()
	{
		if (_token.Length == 0)
		{
			SetStatus("Log in before loading decks.", true);
			return;
		}
		Fire("/api/decks", "GET", null, auth: true);
	}

	private void OnLogin() => Fire("/api/auth/login", "POST", JsonSerializer.Serialize(new { username = _userEdit.Text, password = _passEdit.Text }), auth: false);

	private void OnRegister() => Fire("/api/auth/register", "POST", JsonSerializer.Serialize(new { username = _userEdit.Text, email = "", password = _passEdit.Text }), auth: false);

	// -------------------------------------------------------------- http --

	private void Fire(string path, string method, string? body, bool auth)
	{
		_lastPath = path;
		_lastMethod = method;

		var headers = new List<string> { "Content-Type: application/json" };
		if (auth && _token.Length > 0)
			headers.Add($"Authorization: Bearer {_token}");

		var error = _http.Request(ApiBase + _lastPath, headers.ToArray(), MethodFrom(_lastMethod), body ?? "");
		SetStatus(error == Error.Ok
			? $"Sending {_lastMethod} {_lastPath}..."
			: $"Request could not start (error {error}).", error != Error.Ok);
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
			SetStatus($"Server error {responseCode}: {Truncate(text)}", true);
			return;
		}

		if (_lastPath.StartsWith("/api/auth") && text.Length > 0)
		{
			try
			{
				using var doc = JsonDocument.Parse(text);
				var root = doc.RootElement;
				_token = root.GetProperty("token").GetString() ?? "";
				var username = root.GetProperty("username").GetString() ?? "";
				Global.Instance.Token = _token;
				Global.Instance.Username = _token.Length > 0 ? username : Global.Instance.Username;
				_authLabel.Text = _token.Length > 0 ? $"Logged in as {username}" : "Not logged in";
				SetStatus("Authenticated.", false);
				return;
			}
			catch (Exception)
			{
				// fall through
			}
		}

		if (_lastPath == "/api/decks" && _lastMethod == "GET")
		{
			try
			{
				using var doc = JsonDocument.Parse(text);
				var decks = doc.RootElement;
				if (decks.GetArrayLength() == 0)
				{
					SetStatus("No saved decks yet.", false);
					return;
				}
				var first = decks[0];
				_selectedDeckId = first.GetProperty("id").GetString() ?? "";
				_deck.Clear();
				var name = first.GetProperty("name").GetString() ?? "";
				foreach (var c in first.GetProperty("cards").EnumerateArray())
				{
					var cardId = c.GetProperty("cardId").GetString() ?? "";
					var count = c.GetProperty("count").GetInt32();
					if (cardId.Length > 0)
						_deck[cardId] = count;
				}
				RefreshDeckView();
				SetStatus($"Loaded deck '{name}' ({CardCount()} cards).", false);
				return;
			}
			catch (Exception)
			{
				SetStatus($"Could not parse decks response: {Truncate(text)}", true);
				return;
			}
		}

		var okMessage = _lastMethod == "POST" ? "Deck created." : _lastMethod == "PUT" ? "Deck updated." : "OK.";
		SetStatus($"{okMessage} ({responseCode})", false);
	}

	private static string Truncate(string s, int max = 160)
		=> s.Length <= max ? s : s[..max] + "...";
}
