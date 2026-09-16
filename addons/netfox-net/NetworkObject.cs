using Godot;
using Netfox.Core.Time;

namespace Netfox;

/// <summary>
/// One replicated object. While its root is this peer's multiplayer authority it sends the <c>[Synced]</c> properties of
/// its subtree every tick; otherwise it plays them back from the authority's samples, a few ticks behind, on the clock
/// shared by everything that peer sends. See docs/design/distributed-authority.md.
/// <para>
/// A nested <see cref="NetworkObject"/> owns its own subtree: a crate carried inside a player is not part of the
/// player.
/// </para>
/// </summary>
public partial class NetworkObject : Node
{
    /// <summary>The stack this object belongs to; resolved when it enters the tree.</summary>
    public NetfoxContext Context { get; private set; } = NetfoxContext.Default;

    /// <summary>The node that is the object: authority, identity and the synced subtree. The parent by default.</summary>
    [Export] public Node? Root { get; set; }

    internal List<(Node Node, NodePath Property, bool Interpolate)> Properties { get; } = new();
    internal SampleTrack<Sample> Track { get; } = new();
    internal bool TeleportPending { get; set; }

    /// <summary>True when this peer simulates the object and sends its state.</summary>
    public bool IsAuthority => Root!.IsMultiplayerAuthority();

    /// <summary>The next state this peer sends applies without interpolation on the others: a respawn, not a flight.</summary>
    public void Teleport() => TeleportPending = true;

    public override void _EnterTree()
    {
        Context = NetfoxContext.For(this);
        Root ??= GetParent();
    }

    public override void _Ready()
    {
        Gather(Root!);
        Context.NetworkObjectServer.Register(this);
    }

    public override void _ExitTree() => Context.NetworkObjectServer?.Deregister(this);

    private void Gather(Node node)
    {
        if (node is ISyncedProperties synced)
            foreach (var property in synced.GetSyncedProperties())
                Properties.Add((node, new NodePath(property.Path), property.Interpolate));

        foreach (var child in node.GetChildren())
        {
            if (child is NetworkObject) continue;
            if (child != this && HasOwnObject(child)) continue;
            Gather(child);
        }
    }

    private bool HasOwnObject(Node node)
    {
        foreach (var child in node.GetChildren())
            if (child is NetworkObject other && other != this) return true;
        return false;
    }

    internal sealed class Sample(Variant[] values, bool teleport)
    {
        public Variant[] Values { get; } = values;
        public bool Teleport { get; } = teleport;
    }
}
