using Godot;

namespace Netfox.Tests;

/// <summary>
/// One netfox stack in a test: its own <see cref="NetfoxContext"/> (so its own servers), its own MultiplayerAPI rooted
/// at this node, and a <see cref="LoopbackMultiplayerPeer"/> for it. Two of these in one SceneTree make a host and a
/// client that talk to each other in-process.
/// </summary>
public partial class NetfoxStack : NetfoxContextRoot
{
    public LoopbackMultiplayerPeer Peer { get; private set; } = null!;
    public MultiplayerApi Api { get; private set; } = null!;

    /// <summary>Peers address nodes by their path below the multiplayer root, so both stacks must have the same shape.</summary>
    public static NetfoxStack Create(Node parent, string name, LoopbackNetwork network, int peerId)
    {
        var stack = new NetfoxStack { Name = name };
        var api = MultiplayerApi.CreateDefaultInterface();

        // Routing is by path, so this can be set before the node exists
        parent.GetTree().SetMultiplayer(api, new NodePath($"{parent.GetPath()}/{name}"));
        parent.AddChild(stack);

        stack.Api = api;
        stack.Peer = network.CreatePeer(peerId);
        api.MultiplayerPeer = stack.Peer;
        return stack;
    }

    /// <summary>Drops the current peer, the way leaving a lobby does.</summary>
    public void Disconnect()
    {
        Api.MultiplayerPeer?.Close();
        Api.MultiplayerPeer = null;
    }

    /// <summary>Joins a new session with the same servers, the way rejoining from a lobby does.</summary>
    public void Reconnect(LoopbackNetwork network, int peerId)
    {
        Peer = network.CreatePeer(peerId);
        Api.MultiplayerPeer = Peer;
    }

    /// <summary>Frees the stack and drops the MultiplayerAPI that was bound to its path.</summary>
    public void Teardown()
    {
        var path = GetPath();
        var tree = GetTree();

        Api.MultiplayerPeer?.Close();
        Api.MultiplayerPeer = null;

        GetParent().RemoveChild(this);
        QueueFree();

        tree.SetMultiplayer(null, path);
    }
}
