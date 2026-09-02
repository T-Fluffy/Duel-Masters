using System;
using System.Collections.Generic;
using System.Linq;
using DuelMasters.Domain;
using DuelMasters.Gameplay.CardView;
using DuelMasters.Resources;
using Godot;

namespace DuelMasters.Scenes.Arena;

/// <summary>
/// Phase 3: local 2.5D hotseat arena. Renders both players' zones from the live
/// <see cref="DuelGame"/> state and maps pointer clicks onto domain actions -
/// charging mana, summoning, casting, attacking (with blockers) and ending turns.
///
/// This is intentionally a thin presentation layer: every rule check happens in
/// the shared <c>DuelMasters.Domain</c> engine, so the visual sandbox can never
/// diverge from the authoritative rules used by the backend and the tests.
/// </summary>
public partial class Arena : Control
{
    private enum Mode { Idle, SelectHand, SelectTarget, SelectBlock }

    private DuelGame _game = null!;
    private readonly Dictionary<string, string> _artByCardId = new();

    // UI: zone containers rebuilt each refresh.
    private VBoxContainer _oppoHand = null!;
    private VBoxContainer _oppoMana = null!;
    private VBoxContainer _oppoBattle = null!;
    private VBoxContainer _shieldsTop = null!;
    private VBoxContainer _playerShields = null!;
    private VBoxContainer _playerBattle = null!;
    private VBoxContainer _playerMana = null!;
    private VBoxContainer _playerHand = null!;

    private Label _oppHandTitle = null!;
    private Label _oppManaTitle = null!;
    private Label _oppBattleTitle = null!;
    private Label _shieldsTopTitle = null!;
    private Label _playerShieldsTitle = null!;
    private Label _playerBattleTitle = null!;
    private Label _playerManaTitle = null!;
    private Label _playerHandTitle = null!;
    private Label _graveLabel = null!;
    private Label _turnLabel = null!;
    private Label _promptLabel = null!;
    private HBoxContainer _actionBar = null!;
    private Button _endTurn = null!;

    // Interaction state.
    private Mode _mode = Mode.Idle;
    private int _attackerIndex = -1;

    public override void _Ready()
    {
        BuildLayout();
        StartHotseat();
    }

    // ------------------------------------------------------------ initialization

    private void StartHotseat()
    {
        _artByCardId.Clear();
        foreach (var r in CardCatalog.Load())
            _artByCardId[r.Card.Id] = r.ImagePath;

        var p1 = new Player("Player 1", CardCatalog.BuildStarterDeck(101));
        var p2 = new Player("Player 2", CardCatalog.BuildStarterDeck(202));
        _game = new DuelGame(p1, p2);

        _game.StartGame(shuffle: true);
        _game.StartTurn();
        _game.Draw();

        ResetInteraction();
        Refresh();
    }

    // ------------------------------------------------------------------- layout

    private void BuildLayout()
    {
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 24);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_right", 24);
        margin.AddThemeConstantOverride("margin_bottom", 16);
        AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        margin.AddChild(root);

        // Opponent board row.
        var oppRow = new HBoxContainer();
        oppRow.AddThemeConstantOverride("separation", 20);
        _oppoHand = BuildZone("OPPONENT HAND", out _oppHandTitle);
        _oppoMana = BuildZone("OPPONENT MANA", out _oppManaTitle);
        _oppoBattle = BuildZone("OPPONENT BATTLE", out _oppBattleTitle);
        _shieldsTop = BuildZone("SHIELDS", out _shieldsTopTitle);
        oppRow.AddChild(_oppoHand);
        oppRow.AddChild(_oppoMana);
        oppRow.AddChild(_oppoBattle);
        oppRow.AddChild(_shieldsTop);
        root.AddChild(oppRow);

        // Status line.
        _turnLabel = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
        _turnLabel.AddThemeFontSizeOverride("font_size", 22);
        root.AddChild(_turnLabel);

        // Player board row (mirrored).
        var playerRow = new HBoxContainer();
        playerRow.AddThemeConstantOverride("separation", 20);
        _playerShields = BuildZone("SHIELDS", out _playerShieldsTitle);
        _playerBattle = BuildZone("YOUR BATTLE", out _playerBattleTitle);
        _playerMana = BuildZone("YOUR MANA", out _playerManaTitle);
        _playerHand = BuildZone("YOUR HAND", out _playerHandTitle);
        playerRow.AddChild(_playerShields);
        playerRow.AddChild(_playerBattle);
        playerRow.AddChild(_playerMana);
        playerRow.AddChild(_playerHand);
        root.AddChild(playerRow);

        _graveLabel = new Label { Text = "" };
        root.AddChild(_graveLabel);

        _promptLabel = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
        _promptLabel.AddThemeFontSizeOverride("font_size", 16);
        root.AddChild(_promptLabel);

        // Actions + end turn.
        var footer = new HBoxContainer();
        footer.AddThemeConstantOverride("separation", 16);
        _actionBar = new HBoxContainer();
        _actionBar.AddThemeConstantOverride("separation", 10);
        footer.AddChild(_actionBar);

        _endTurn = new Button { Text = "End Turn" };
        _endTurn.Pressed += OnEndTurn;
        footer.AddChild(_endTurn);
        root.AddChild(footer);
    }

    private VBoxContainer BuildZone(string caption, out Label titleLabel)
    {
        var box = new VBoxContainer();
        box.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        box.AddThemeConstantOverride("separation", 6);

        var header = new HBoxContainer();
        titleLabel = new Label { Text = caption };
        titleLabel.AddThemeFontSizeOverride("font_size", 13);
        header.AddChild(titleLabel);
        box.AddChild(header);

        var flow = new HFlowContainer();
        flow.AddThemeConstantOverride("h_separation", 8);
        flow.AddThemeConstantOverride("v_separation", 8);
        box.AddChild(flow);
        return box;
    }

    // ---------------------------------------------------------- interaction flow

    private void OnEndTurn()
    {
        if (_game.IsGameOver)
            return;
        try
        {
            _game.EndMainPhase();
            _game.EndTurn();
            if (!_game.IsGameOver)
            {
                _game.StartTurn();
                _game.Draw();
            }
        }
        catch (RuleViolationException ex)
        {
            Notice(ex.Message);
        }
        ResetInteraction();
        Refresh();
    }

    private static HFlowContainer GetFlow(Control box)
    {
        foreach (var child in box.GetChildren())
            if (child is HFlowContainer flow)
                return flow;
        var created = new HFlowContainer();
        created.AddThemeConstantOverride("h_separation", 8);
        created.AddThemeConstantOverride("v_separation", 8);
        box.AddChild(created);
        return created;
    }

    private void OnHandClicked(Player owner, int index)
    {
        if (_game.IsGameOver || !ReferenceEquals(owner, ThePlayer()))
            return;
        if (_game.Phase != GamePhase.Main)
        {
            Notice("You can only play cards during your Main phase.");
            return;
        }
        if (_mode == Mode.SelectBlock)
            return;

        _mode = Mode.SelectHand;
        RefreshActionBar(index);
    }

    private void RefreshActionBar(int handIndex)
    {
        ClearActions();
        var player = ThePlayer();
        var instance = player.Hand[handIndex];
        var card = instance.Card;
        var affordable = _game.CanAfford(player, card);

        if (!_game.ManaChargedThisTurn)
            AddAction($"Charge Mana: {card.Name}", () => DoCharge(handIndex));

        if (card.IsCreature && !_game.HasAttackedThisTurn)
            AddAction($"Summon: {card.Name}", () => DoSummon(handIndex), affordable);
        else if (card.CardType == CardType.Spell && !_game.HasAttackedThisTurn)
            AddAction($"Cast: {card.Name}", () => DoCast(handIndex), affordable);
    }

    private void DoCharge(int index) => Safe(() => _game.PlayManaToManaZone(index));
    private void DoSummon(int index)
    {
        Safe(() =>
        {
            var summoned = _game.SummonCreature(index);
            if (_game.IsGameOver || _game.Winner is not null)
                Notice($"{summoned.Card.Name} summoned.");
        });
    }

    private void DoCast(int index) => Safe(() => _game.CastSpell(index));

    private void OnBattleClicked(bool isPlayer, int index)
    {
        if (_game.IsGameOver)
            return;
        var mine = isPlayer;

        switch (_mode)
        {
            case Mode.Idle:
            case Mode.SelectHand:
                if (mine)
                {
                    var candidate = ThePlayer().BattleZone[index];
                    if (candidate.IsTapped || candidate.IsSummoningSick)
                    {
                        Notice($"{candidate.Card.Name} cannot attack yet.");
                        return;
                    }
                    _attackerIndex = index;
                    _mode = Mode.SelectTarget;
                    Prompt("Choose a target: a tapped enemy creature, or the enemy shields. Click an enemy zone to attack.");
                }
                break;

            case Mode.SelectTarget:
                if (mine)
                {
                    var candidate = ThePlayer().BattleZone[index];
                    if (!candidate.IsTapped && !candidate.IsSummoningSick)
                    {
                        _attackerIndex = index;
                        Prompt("Pick a target for the new attacker.");
                    }
                    break;
                }
                var target = TheOpponent().BattleZone[index];
                if (target.IsTapped)
                {
                    Safe(() => _game.AttackCreature(_attackerIndex, index));
                    ResetInteraction();
                }
                else
                {
                    Notice("You may only attack a tapped creature.");
                }
                break;

            case Mode.SelectBlock:
                if (IsDefenderBlocker(index))
                {
                    Safe(() => _game.AttackPlayer(_attackerIndex, _game.Opponent, index));
                    ResetInteraction();
                }
                break;
        }
        Refresh();
    }

    private bool IsDefenderBlocker(int index)
    {
        var defender = _game.Opponent;
        return index >= 0 && index < defender.BattleZone.Count
            && defender.BattleZone[index].Card.HasKeyword(Keyword.Blocker)
            && !defender.BattleZone[index].IsTapped;
    }

    private void OnShieldsClicked()
    {
        if (_game.IsGameOver)
            return;

        // Clicking the shields during targeting selects the player as the attack
        // target; during a block prompt it bypasses blocking and attacks anyway.
        if (_mode == Mode.SelectTarget || _mode == Mode.SelectHand)
        {
            if (_attackerIndex < 0)
            {
                Prompt("Select one of your ready creatures to attack first.");
                return;
            }

            // Does the defender have an eligible blocker?
            if (_game.Opponent.BattleZone.Any(c => c.Card.IsCreature && !c.IsTapped && c.Card.HasKeyword(Keyword.Blocker)))
            {
                _mode = Mode.SelectBlock;
                Prompt("The defender may block. Click a Blocker creature, or click the shields again to attack anyway.");
                return;
            }

            Safe(() => _game.AttackPlayer(_attackerIndex));
            Refresh();
            return;
        }

        if (_mode == Mode.SelectBlock && _attackerIndex >= 0)
        {
            Safe(() => _game.AttackPlayer(_attackerIndex));
            Refresh();
        }
    }

    // ------------------------------------------------------------ ui helpers

    private void AddAction(string label, Action onClick, bool enabled = true)
    {
        var b = new Button { Text = label, Disabled = !enabled };
        b.Pressed += () =>
        {
            onClick();
            ResetInteraction();
            Refresh();
        };
        _actionBar.AddChild(b);
    }

    private void ClearActions()
    {
        foreach (var child in _actionBar.GetChildren().OfType<Control>().ToList())
            child.QueueFree();
    }

    private void Safe(Action action)
    {
        try
        {
            action();
            ResetInteraction();
        }
        catch (RuleViolationException ex)
        {
            Notice(ex.Message);
        }
        Refresh();
    }

    private void ResetInteraction()
    {
        _mode = Mode.Idle;
        _attackerIndex = -1;
        ClearActions();
    }

    private void Prompt(string message)
    {
        _promptLabel.Text = message;
        _promptLabel.Modulate = new Color(1f, 0.95f, 0.7f);
    }

    private void Notice(string message)
    {
        _promptLabel.Text = message;
        _promptLabel.Modulate = message.Length == 0 ? Colors.White : new Color(1f, 0.6f, 0.5f);
    }

    private void Refresh()
    {
        if (_game is null)
            return;

        BuildZoneInto(_playerHand, ThePlayer().Hand, backs: false, _playerHandTitle);
        BuildZoneInto(_oppoHand, TheOpponent().Hand, backs: true, _oppHandTitle);
        BuildZoneInto(_playerMana, ThePlayer().ManaZone, backs: false, _playerManaTitle);
        BuildZoneInto(_oppoMana, TheOpponent().ManaZone, backs: false, _oppManaTitle);
        BuildZoneInto(_playerBattle, ThePlayer().BattleZone, backs: false, _playerBattleTitle);
        BuildZoneInto(_oppoBattle, TheOpponent().BattleZone, backs: false, _oppBattleTitle);
        BuildShields(_playerShields, ThePlayer().ShieldCount, _playerShieldsTitle);
        BuildShields(_shieldsTop, TheOpponent().ShieldCount, _shieldsTopTitle);

        _graveLabel.Text =
            $"GRAVEYARDS   P1: {_game.Player1.Graveyard.Count}   |   P2: {_game.Player2.Graveyard.Count}";

        var seat = _game.ActivePlayer == _game.Player1 ? "bottom" : "top";
        _turnLabel.Text = _game.IsGameOver
            ? $"Game over - {_game.Winner!.Name} wins!"
            : $"{_game.ActivePlayer.Name}'s turn ({seat})  |  Turn {_game.TurnNumber}  |  {_game.Phase}";

        _endTurn.Disabled = _game.IsGameOver;
        if (!_game.IsGameOver && _game.Phase == GamePhase.End)
            _endTurn.Disabled = true;

        AttachHandInteraction();
        AttachBattleInteraction();
    }

    private void BuildZoneInto(VBoxContainer box, IReadOnlyList<CardInstance> zone, bool backs, Label title)
    {
        var flow = GetFlow(box);
        ClearFlow(flow);
        for (var i = 0; i < zone.Count; i++)
        {
            var inst = zone[i];
            var view = new CardView(inst.Card, backs ? null : ArtFor(inst.Card), faceDown: backs);
            view.SnapTapped(inst.IsTapped);
            flow.AddChild(view);
        }
        title.Text = $"{TitleOf(box)}  ({zone.Count})";
    }

    private void BuildShields(VBoxContainer box, int count, Label title)
    {
        var flow = GetFlow(box);
        ClearFlow(flow);
        for (var i = 0; i < count; i++)
        {
            var view = new CardView(null, faceDown: true);
            flow.AddChild(view);
        }
        title.Text = $"{TitleOf(box)}  ({count})";
    }

    private static void ClearFlow(HFlowContainer flow)
    {
        foreach (var child in flow.GetChildren().OfType<Node>().ToList())
            child.QueueFree();
    }

    private static string TitleOf(VBoxContainer box)
    {
        foreach (var child in box.GetChildren())
            if (child is Label l)
                return l.Text.Split("  (").First();
        return "";
    }

    private void AttachHandInteraction()
    {
        WireZoneCards(_playerHand);
    }

    private void AttachBattleInteraction()
    {
        WireZoneCards(_playerBattle);
        WireZoneCards(_oppoBattle);
        WireZoneCards(_shieldsTop);
        WireZoneCards(_oppoHand);
        WireZoneCards(_oppoMana);
        WireZoneCards(_playerMana);
        WireZoneCards(_playerShields);
    }

    private void WireZoneCards(VBoxContainer box)
    {
        var flow = GetFlow(box);
        var views = flow.GetChildren().OfType<CardView>().ToList();

        if (ReferenceEquals(box, _playerHand))
        {
            for (var i = 0; i < views.Count; i++)
            {
                var idx = i;
                views[i].Clicked += _ => OnHandClicked(ThePlayer(), idx);
            }
        }
        else if (ReferenceEquals(box, _playerBattle))
        {
            for (var i = 0; i < views.Count; i++)
            {
                var idx = i;
                views[i].Clicked += _ => OnBattleClicked(isPlayer: true, idx);
            }
        }
        else if (ReferenceEquals(box, _oppoBattle))
        {
            for (var i = 0; i < views.Count; i++)
            {
                var idx = i;
                views[i].Clicked += _ => OnBattleClicked(isPlayer: false, idx);
            }
        }
        else if (ReferenceEquals(box, _shieldsTop))
        {
            foreach (var v in views)
                v.Clicked += _ => OnShieldsClicked();
        }
    }

    private Player ThePlayer() => _game.ActivePlayer;
    private Player TheOpponent() => _game.Opponent;
    private string? ArtFor(Card card) => _artByCardId.TryGetValue(card.Id, out var p) ? p : null;
}
