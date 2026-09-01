using Godot;

namespace DuelMasters.Core.MainGame;

/// <summary>
/// Main entry point for the game. Owns the high-level screen flow (main menu,
/// match setup, board). Cards-specific screen roots hang off this node.
/// </summary>
[GlobalClass]
public partial class MainGame : Node
{
    // UI Screen Root Nodes (FUTURE).
    [Export] private Control? _menuRoot;
    [Export] private Control? _matchRoot;
    [Export] private Control? _hudRoot;
    [Export] private Control? _transitionRoot;

    public override void _Ready()
    {
        // FUTURE (main menu): Load the initial screen.
    }

    public override void _Input(InputEvent @event)
    {
        if (!OS.IsDebugBuild())
            return;

        if (@event.IsActionPressed("debug_quit"))
            QuitGame();
    }

    /// <summary>Called to quit the application.</summary>
    public void QuitGame()
    {
        GetTree().Root.PropagateNotification((int)NotificationWMCloseRequest);
        GetTree().Quit();
    }
}