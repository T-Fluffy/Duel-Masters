using Godot;

namespace DuelMasters.Core.Autoload;

/// <summary>
/// Global autoload singleton. Registered in Project Settings under the name "Global".
/// Access it from any script via <c>GetNode&lt;Global&gt;("/root/Global")</c>.
/// App-wide cross-scene state (settings, event bus, match/network handles) hangs off this node.
/// </summary>
[GlobalClass]
public partial class Global : Node
{
    /// <summary>The username of the authenticated player, or empty if not logged in.</summary>
    public string Username { get; set; } = "";

    /// <summary>The JWT returned by the auth backend, or empty if not logged in.</summary>
    public string Token { get; set; } = "";

    public bool IsAuthenticated => Token.Length > 0;

    /// <summary>Looks up the Global autoload from anywhere in the scene tree.</summary>
    public static Global Instance =>
        Engine.GetMainLoop() is SceneTree tree
            ? tree.Root.GetNode<Global>("/root/Global") ?? throw new System.InvalidOperationException("Global autoload not found.")
            : throw new System.InvalidOperationException("No active SceneTree.");

    public override void _Ready()
    {
        // Diagnostic: report the real OS window state at launch so we can tell whether the
        // game runs embedded (editor game view) or as a standalone resizable window.
        var window = GetWindow();
        var wid = window.GetWindowId();
        GD.Print("[Global] windowId=" + wid +
                 " size=" + DisplayServer.WindowGetSize(wid) +
                 " embedded=" + window.IsEmbedded() +
                 " mode=" + DisplayServer.WindowGetMode());
    }
}
