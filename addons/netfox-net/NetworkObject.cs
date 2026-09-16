using Godot;
using Netfox.Core.Time;

namespace Netfox;

/// <summary>
/// One replicated object. While its root is this peer's multiplayer authority it sends the <c>[Synced]</c> properties of
/// its subtree every tick; otherwise it plays them back from the authority's samples, a few ticks behind, on the clock
/// shared by everything that peer sends. See docs/design/distributed-authority.md.
/// <para>
/// Authority moves at runtime: <see cref="TryTakeAuthority"/> when this peer touches the object, <see cref="TryGrab"/>
/// when it holds it, <see cref="Release"/> and <see cref="ReturnToHost"/> after. Every change applies here at once and
/// goes to the host, which accepts it or corrects this peer. A change is newer when its ownership sequence is higher,
/// or equal with a higher authority sequence, so a grab beats a touch.
/// </para>
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

    /// <summary>Whether other peers may take authority or ownership. Off for players: their character stays theirs.</summary>
    [Export] public bool Transferable { get; set; } = true;

    /// <summary>The peer holding the object, or 0 when nobody does.</summary>
    public int Holder { get; private set; }

    public int AuthoritySequence { get; private set; }
    public int OwnershipSequence { get; private set; }

    /// <summary>Raised after the authority or the owner changed, on every peer.</summary>
    public event Action? AuthorityChanged;

    /// <summary>
    /// Raised on a peer playing the object back, for every sample of the authority's state it kept: the tick it is
    /// for. What arrived, as against what was sent - for diagnostics and checks.
    /// </summary>
    public event Action<int>? SampleReceived;

    internal void RaiseSampleReceived(int tick) => SampleReceived?.Invoke(tick);

    /// <summary>
    /// Raised on the authority for every state it sends: the tick. Not every tick - an unchanged object is sent only
    /// as a heartbeat - so this is the ground truth a check compares playback against.
    /// </summary>
    public event Action<int>? SampleSent;

    internal void RaiseSampleSent(int tick) => SampleSent?.Invoke(tick);

    internal List<(Node Node, NodePath Property, bool Interpolate)> Properties { get; } = new();
    internal SampleTrack<Sample> Track { get; } = new();
    internal bool TeleportPending { get; set; }

    /// <summary>What this peer last sent for the object, and when: an unchanged object is not sent again for a while.</summary>
    internal byte[]? LastSentBody { get; set; }
    internal int LastSentTick { get; set; }

    /// <summary>True when this peer simulates the object and sends its state.</summary>
    public bool IsAuthority => Root!.IsMultiplayerAuthority();

    public int Authority => Root!.GetMultiplayerAuthority();

    private int LocalPeer => Root!.Multiplayer.GetUniqueId();

    /// <summary>The next state this peer sends applies without interpolation on the others: a respawn, not a flight.</summary>
    public void Teleport() => TeleportPending = true;

    /// <summary>
    /// Takes authority over a free object this peer touched. False when it is held by someone else, or when this peer
    /// is not connected; true means applied here and sent, not yet accepted by the host.
    /// </summary>
    public bool TryTakeAuthority()
    {
        if (!Transferable || (Holder != 0 && Holder != LocalPeer)) return false;
        if (IsAuthority) return true;
        return Request(LocalPeer, Holder, AuthoritySequence + 1, OwnershipSequence);
    }

    /// <summary>Takes ownership and authority. False when someone else holds it.</summary>
    public bool TryGrab()
    {
        if (!Transferable || (Holder != 0 && Holder != LocalPeer)) return false;
        if (Holder == LocalPeer) return true;
        return Request(LocalPeer, LocalPeer, AuthoritySequence + 1, OwnershipSequence + 1);
    }

    /// <summary>Lets go of a held object. This peer keeps simulating it until someone else touches it.</summary>
    public bool Release()
        => Holder == LocalPeer && Request(LocalPeer, 0, AuthoritySequence, OwnershipSequence + 1);

    /// <summary>Hands a free object this peer simulates back to the host, typically once it has come to rest.</summary>
    public bool ReturnToHost()
        => IsAuthority && Holder == 0 && LocalPeer != HostPeer && Request(HostPeer, 0, AuthoritySequence + 1, OwnershipSequence);

    /// <summary>
    /// Raised on the authority, exactly once per <see cref="SendToAuthority"/> call anywhere: the peer that sent it and
    /// what it sent. A push, damage, "this projectile hit me".
    /// </summary>
    public event Action<int, Variant>? EventReceived;

    /// <summary>
    /// Delivers <paramref name="payload"/> to whoever is this object's authority, reliably and exactly once, even if
    /// authority moves while it is on its way: the transport does not duplicate, and a peer that is no longer the
    /// authority passes the event on instead of raising it. On the authority itself it is raised at once.
    /// </summary>
    public void SendToAuthority(Variant payload)
    {
        if (IsAuthority) Receive(LocalPeer, payload);
        else Context.NetworkObjectServer.SendEvent(this, Authority, LocalPeer, payload, hops: 0);
    }

    internal void Receive(int origin, Variant payload) => EventReceived?.Invoke(origin, payload);

    internal const int HostPeer = 1;

    private bool Request(int authority, int owner, int authoritySequence, int ownershipSequence)
    {
        // A change nobody hears about would leave this peer disagreeing with everyone for good
        if (Root!.Multiplayer.MultiplayerPeer is not { } peer || peer is OfflineMultiplayerPeer
            || peer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Connected)
            return false;

        Apply(authority, owner, authoritySequence, ownershipSequence);
        Context.NetworkObjectServer.SubmitAuthority(this);
        return true;
    }

    /// <summary>True when (<paramref name="ownershipSequence"/>, <paramref name="authoritySequence"/>) is newer than what this object has.</summary>
    internal bool IsNewer(int authoritySequence, int ownershipSequence)
        => ownershipSequence > OwnershipSequence
           || (ownershipSequence == OwnershipSequence && authoritySequence > AuthoritySequence);

    internal void Apply(int authority, int owner, int authoritySequence, int ownershipSequence)
    {
        var changed = authority != Authority || owner != Holder;
        if (authority != Authority)
        {
            SetAuthority(Root!, authority);
            if (IsAuthority && !Shown) SetShown(true);
            // Samples are stamped on the previous authority's clock; the new one sends its own, at once even at rest
            Track.Clear();
            LastSentBody = null;
        }

        Holder = owner;
        AuthoritySequence = authoritySequence;
        OwnershipSequence = ownershipSequence;
        if (changed) AuthorityChanged?.Invoke();
    }

    private static void SetAuthority(Node node, int peer)
    {
        node.SetMultiplayerAuthority(peer, recursive: false);
        foreach (var child in node.GetChildren())
            if (!HasOwnObject(child, except: null) || child is NetworkObject)
                SetAuthority(child, peer);
    }

    public override void _EnterTree()
    {
        Context = NetfoxContext.For(this);
        Root ??= GetParent();
    }

    public override void _Ready()
    {
        Gather(Root!);
        // Nothing to show until playback reaches this object's first sample: a projectile would otherwise hang at the
        // muzzle for the playback delay before it flies
        if (!IsAuthority) SetShown(false);
        Context.NetworkObjectServer.Register(this);
    }

    internal bool Shown { get; private set; } = true;

    internal void SetShown(bool shown)
    {
        Shown = shown;
        switch (Root)
        {
            case Node3D node: node.Visible = shown; break;
            case CanvasItem item: item.Visible = shown; break;
        }
    }

    public override void _ExitTree() => Context.NetworkObjectServer?.Deregister(this);

    private void Gather(Node node)
    {
        if (node is ISyncedProperties synced)
            foreach (var property in synced.GetSyncedProperties())
                Properties.Add((node, new NodePath(property.Path), property.Interpolate));

        foreach (var child in node.GetChildren())
        {
            if (child is NetworkObject || HasOwnObject(child, except: this)) continue;
            Gather(child);
        }
    }

    private static bool HasOwnObject(Node node, NetworkObject? except)
    {
        foreach (var child in node.GetChildren())
            if (child is NetworkObject other && other != except) return true;
        return false;
    }

    internal sealed class Sample(Variant[] values, bool teleport)
    {
        public Variant[] Values { get; } = values;
        public bool Teleport { get; } = teleport;
    }
}
