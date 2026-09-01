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
    // FUTURE: App-wide systems and shared state land here.
}