using System;
using System.Collections.Generic;
using System.Linq;
using DuelMasters.Domain.Networking;
using DuelMasters.Gameplay.CardView;
using DuelMasters.Networking;
using DuelMasters.Resources;
using DuelMasters.UI.Settings;
using Godot;

namespace DuelMasters.Scenes.NetworkArena;

/// <summary>
/// Phase 4: rendered view of the server-authoritative duel. The rules engine runs
/// only on the backend; this scene draws the viewer-relative <see cref="DuelGameState"/>
/// pushed by the hub and forwards player intents (mana, summon, cast, attacks, turns)
/// via <see cref="NetworkClient"/>. No local <c>DuelGame</c> exists here.
/// </summary>
public partial class NetworkArena : Control
{
    private enum Mode { Idle, SelectHand, SelectAttacker }

    private DuelGameState _state = null!;
    private readonly Dictionary<string, string> _artByCardId = new();
    private readonly Dictionary<string, DuelMasters.Domain.Card> _cardsByCardId = new();
    private Mode _mode = Mode.Idle;
    private int _attackerIndex = -1;

    private VBoxContainer _oppHand = null!;
    private VBoxContainer _oppShields = null!;
    private VBoxContainer _oppMana = null!;
    private VBoxContainer _oppBattle = null!;
    private VBoxContainer _myShields = null!;
    private VBoxContainer _myBattle = null!;
    private VBoxContainer _myMana = null!;
    private VBoxContainer _myHand = null!;

    private Label _status = null!;
    private Label _grave = null!;
    private Label _prompt = null!;
    private HBoxContainer _actionBar = null!;
    private Button _endTurn = null!;

    public override void _Ready()
    {
        foreach (var r in CardCatalog.Load())
        {
            _artByCardId[r.Card.Id] = r.ImagePath;
            _cardsByCardId[r.Card.Id] = r.Card;
        }
        BuildLayout();
        if (NetworkClient.CurrentState is { } st)
            _state = st;
        Refresh();
    }

    public override void _Process(double delta)
    {
        if (NetworkClient.TryDequeueError(out var err))
            Notice(err);
        if (NetworkClient.TryDequeueWinner(out var winner))
        {
            _endTurn.Disabled = true;
            Notice($"Winner: {winner}");
        }
        if (NetworkClient.TryDequeueState(out var state))
        {
            _state = state;
            Refresh();
        }

        // Auto-advance the Untap -> Draw -> Main steps at the start of my turn.
        if (_state is not null && _state.YourTurn && !_state.IsGameOver)
        {
            if (IsPhase("Untap"))
                NetworkClient.StartTurn();
            else if (IsPhase("Draw"))
                NetworkClient.Draw();
        }
    }

    // --------------------------------------------------------------- layout

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

        var oppRow = new HBoxContainer();
        oppRow.AddThemeConstantOverride("separation", 16);
        _oppHand = BuildZone("OPPONENT HAND", out _);
        _oppShields = BuildZone("SHIELDS", out _);
        _oppMana = BuildZone("OPPONENT MANA", out _);
        _oppBattle = BuildZone("OPPONENT BATTLE", out _);
        oppRow.AddChild(_oppHand);
        oppRow.AddChild(_oppShields);
        oppRow.AddChild(_oppMana);
        oppRow.AddChild(_oppBattle);
        root.AddChild(oppRow);

        _status = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
        _status.AddThemeFontSizeOverride("font_size", 22);
        root.AddChild(_status);

        var myRow = new HBoxContainer();
        myRow.AddThemeConstantOverride("separation", 16);
        _myShields = BuildZone("YOUR SHIELDS", out _);
        _myBattle = BuildZone("YOUR BATTLE", out _);
        _myMana = BuildZone("YOUR MANA", out _);
        _myHand = BuildZone("YOUR HAND", out _);
        myRow.AddChild(_myShields);
        myRow.AddChild(_myBattle);
        myRow.AddChild(_myMana);
        myRow.AddChild(_myHand);
        root.AddChild(myRow);

        _grave = new Label { Text = "" };
        root.AddChild(_grave);

        _prompt = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
        _prompt.AddThemeFontSizeOverride("font_size", 16);
        root.AddChild(_prompt);

        var footer = new HBoxContainer();
        footer.AddThemeConstantOverride("separation", 16);
        _actionBar = new HBoxContainer();
        _actionBar.AddThemeConstantOverride("separation", 10);
        footer.AddChild(_actionBar);
        _endTurn = new Button { Text = "End Turn" };
        _endTurn.Pressed += OnEndTurn;
        footer.AddChild(_endTurn);
        footer.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var leave = new Button { Text = "Leave" };
        leave.Pressed += OnLeave;
        footer.AddChild(leave);
        root.AddChild(footer);

        AddChild(new SceneOptionsMenu { ShowBackToMenu = true });
    }

    private VBoxContainer BuildZone(string caption, out Label title)
    {
        var box = new VBoxContainer();
        box.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        box.AddThemeConstantOverride("separation", 6);
        title = new Label { Text = caption };
        title.AddThemeFontSizeOverride("font_size", 13);
        box.AddChild(title);
        var flow = new HFlowContainer();
        flow.AddThemeConstantOverride("h_separation", 8);
        flow.AddThemeConstantOverride("v_separation", 8);
        box.AddChild(flow);
        return box;
    }

    // --------------------------------------------------------------- actions

    private void OnEndTurn()
    {
        if (_state is null || !_state.YourTurn || _state.IsGameOver)
            return;
        if (IsPhase("Main"))
            NetworkClient.EndMainPhase();
        else if (IsPhase("End"))
            NetworkClient.EndTurn();
    }

    private async void OnLeave()
    {
        await NetworkClient.DisconnectAsync();
        GetTree().ChangeSceneToFile("res://src/scenes/network_lobby/NetworkLobby.tscn");
    }

    private void OnHandClicked(int index)
    {
        if (_state is null || !_state.YourTurn || !IsPhase("Main") || _state.IsGameOver)
        {
            Notice("You can only play cards during your Main phase.");
            return;
        }
        _mode = Mode.SelectHand;
        BuildHandActions(index);
    }

    private void BuildHandActions(int handIndex)
    {
        ClearActions();
        var hand = Me().Hand;
        if (handIndex < 0 || handIndex >= hand.Count)
            return;
        var card = hand[handIndex];

        if (_state.CanPlayMana)
            AddAction($"Charge Mana: {card.Name}", () => NetworkClient.PlayMana(handIndex));

        if (card.CardType == "Creature" && _state.CanSummonOrCast)
            AddAction($"Summon: {card.Name}", () => NetworkClient.SummonCreature(handIndex));
        else if (card.CardType == "Spell" && _state.CanSummonOrCast)
            AddAction($"Cast: {card.Name}", () => NetworkClient.CastSpell(handIndex));
    }

    private void OnBattleClicked(bool mine, int index)
    {
        if (_state is null || !_state.YourTurn || _state.IsGameOver)
            return;

        if (mine && !_state.CanAttack)
        {
            Notice("You cannot attack right now.");
            return;
        }

        if (mine)
        {
            var battle = Me().BattleZone;
            if (index < 0 || index >= battle.Count)
                return;
            var candidate = battle[index];
            if (candidate.IsTapped || candidate.IsSummoningSick)
            {
                Notice($"{candidate.Name} cannot attack yet.");
                return;
            }
            _attackerIndex = index;
            _mode = Mode.SelectAttacker;
            Prompt("Choose a target: a tapped enemy creature, or the enemy shields.");
            return;
        }

        // Opponent battle clicked while selecting an attacker -> attack that creature.
        if (_mode == Mode.SelectAttacker)
        {
            var target = Opp().BattleZone[index];
            if (!target.IsTapped)
            {
                Notice("You may only attack a tapped creature.");
                return;
            }
            NetworkClient.AttackCreature(_attackerIndex, index);
            ResetInteraction();
        }
    }

    private void OnOppShieldsClicked()
    {
        if (_mode == Mode.SelectAttacker && _attackerIndex >= 0)
        {
            NetworkClient.AttackPlayer(_attackerIndex);
            ResetInteraction();
        }
    }

    // --------------------------------------------------------------- render

    private void Refresh()
    {
        if (_state is null)
            return;

        BuildHand(_oppHand, Opp().Hand, faceDown: true);
        BuildShields(_oppShields, Opp().ShieldCount);
        BuildZone(_oppMana, Opp().ManaZone);
        BuildZone(_oppBattle, Opp().BattleZone);

        BuildShields(_myShields, Me().ShieldCount);
        BuildZone(_myBattle, Me().BattleZone);
        BuildZone(_myMana, Me().ManaZone);
        BuildHand(_myHand, Me().Hand, faceDown: false);

        _grave.Text =
            $"GRAVEYARDS   Me: {Me().GraveyardCount}   |   Opponent: {Opp().GraveyardCount}";

        if (_state.IsGameOver)
        {
            _status.Text = "Game over.";
        }
        else
        {
            var you = _state.YourTurn ? "" : " — opponent's turn";
            _status.Text =
                $"{(_state.YourTurn ? "Your" : "Opponent's")} turn ({_state.Phase}){you}  |  Turn {_state.TurnNumber}";
        }

        _endTurn.Disabled = _state.IsGameOver || !_state.YourTurn || !IsPhase("Main") && !IsPhase("End");

        WireHand(_myHand);
        WireBattle(_myBattle, mine: true);
        WireBattle(_oppBattle, mine: false);
        WireZone(_oppShields, OnOppShieldsClicked);
    }

    private void BuildZone(VBoxContainer box, List<CardState> zone)
    {
        var flow = GetFlow(box);
        Clear(flow);
        foreach (var c in zone)
        {
            var view = c.CountOnly
                ? new CardView(null, faceDown: true)
                : new CardView(CardFor(c), ArtFor(c.CardId));
            if (c.IsTapped)
                view.SnapTapped(true);
            flow.AddChild(view);
        }
        UpdateTitle(box, zone.Count);
    }

    private void BuildHand(VBoxContainer box, List<CardState> hand, bool faceDown)
    {
        var flow = GetFlow(box);
        Clear(flow);
        for (var i = 0; i < hand.Count; i++)
        {
            var c = hand[i];
            var view = faceDown || c.CountOnly
                ? new CardView(null, faceDown: true)
                : new CardView(CardFor(c), ArtFor(c.CardId));
            flow.AddChild(view);
        }
        UpdateTitle(box, hand.Count);
    }

    private void BuildShields(VBoxContainer box, int count)
    {
        var flow = GetFlow(box);
        Clear(flow);
        for (var i = 0; i < count; i++)
        {
            var view = new CardView(null, faceDown: true);
            flow.AddChild(view);
        }
        UpdateTitle(box, count);
    }

    private void WireHand(VBoxContainer box)
    {
        var views = GetFlow(box).GetChildren().OfType<CardView>().ToList();
        for (var i = 0; i < views.Count; i++)
        {
            var idx = i;
            views[i].Clicked += _ => OnHandClicked(idx);
        }
    }

    private void WireBattle(VBoxContainer box, bool mine)
    {
        var views = GetFlow(box).GetChildren().OfType<CardView>().ToList();
        for (var i = 0; i < views.Count; i++)
        {
            var idx = i;
            views[i].Clicked += _ => OnBattleClicked(mine, idx);
        }
    }

    private void WireZone(VBoxContainer box, Action onClick)
    {
        foreach (var view in GetFlow(box).GetChildren().OfType<CardView>())
            view.Clicked += _ => onClick();
    }

    // --------------------------------------------------------------- helpers

    private PlayerState Me() => _state.Players.First(p => p.Side == _state.YourSide);
    private PlayerState Opp() => _state.Players.First(p => p.Side != _state.YourSide);
    private bool IsPhase(string phase) => string.Equals(_state.Phase, phase, System.StringComparison.Ordinal);

    private DuelMasters.Domain.Card? CardFor(CardState c) =>
        _cardsByCardId.TryGetValue(c.CardId, out var card) ? card : null;

    private string? ArtFor(string cardId) => _artByCardId.TryGetValue(cardId, out var p) ? p : null;

    private void AddAction(string label, Action onClick)
    {
        var b = new Button { Text = label };
        b.Pressed += () =>
        {
            onClick();
            ResetInteraction();
        };
        _actionBar.AddChild(b);
    }

    private void ClearActions()
    {
        foreach (var child in _actionBar.GetChildren().OfType<Control>().ToList())
            child.QueueFree();
    }

    private void ResetInteraction()
    {
        _mode = Mode.Idle;
        _attackerIndex = -1;
        ClearActions();
        _prompt.Text = "";
    }

    private void Prompt(string message)
    {
        _prompt.Text = message;
        _prompt.Modulate = new Color(1f, 0.95f, 0.7f);
    }

    private void Notice(string message)
    {
        _prompt.Text = message;
        _prompt.Modulate = new Color(1f, 0.6f, 0.5f);
    }

    private static HFlowContainer GetFlow(VBoxContainer box)
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

    private static void Clear(HFlowContainer flow)
    {
        foreach (var child in flow.GetChildren().OfType<Node>().ToList())
            child.QueueFree();
    }

    private static void UpdateTitle(VBoxContainer box, int count)
    {
        foreach (var child in box.GetChildren())
        {
            if (child is Label l)
            {
                var baseName = l.Text.Split("  (").First();
                l.Text = $"{baseName}  ({count})";
            }
        }
    }
}
