using System.Reflection;
using Godot;
using Godot.Collections;
using Netfox.Core.Logging;
using Netfox.Core.Time;
using Netfox.Internal;

namespace Netfox;

/// <summary>
/// One replicated object. While its root is this peer's multiplayer authority it sends the root's transform, velocity
/// for a physics body, and the <c>[Synced]</c> properties of its subtree; otherwise it plays them back from the
/// authority's samples, a few ticks behind, on the clock shared by everything that peer sends. See
/// docs/design/distributed-authority.md.
/// <para>
/// <see cref="Kind"/> decides how authority moves, and for a physics body the library does the rest: it freezes the
/// body where another peer simulates it, passes authority on contact and hands a settled body back to the host.
/// </para>
/// <para>
/// A nested <see cref="NetworkObject"/> owns its own subtree: a crate carried inside a player is not part of the
/// player.
/// </para>
/// </summary>
[Tool]
public partial class NetworkObject : Node
{
    private static readonly NetfoxLogger Logger = NetfoxLogger.ForNetfox("NetworkObject");

    /// <summary>How authority over an object moves, named by the rule rather than by an example.</summary>
    public enum ObjectKind
    {
        /// <summary>From the root's type: character bodies and plain nodes are Personal, rigid bodies Shared, the rest World.</summary>
        Auto,
        /// <summary>Authority stays with one peer and passes on contact: a player, a projectile, a grenade.</summary>
        Personal,
        /// <summary>Taken by touch or grab, and passes on contact: a crate, a ball.</summary>
        Shared,
        /// <summary>Authority stays put and does not pass on contact: a lift, a door, the match score.</summary>
        World,
        /// <summary><see cref="Transferable"/> and <see cref="SpreadsAuthority"/> by hand, and no physics handling.</summary>
        Custom,
    }

    /// <summary>The stack this object belongs to; resolved when it enters the tree.</summary>
    public NetfoxContext Context { get; private set; } = NetfoxContext.Default;

    /// <summary>The node that is the object: authority, identity and the synced subtree. The parent by default.</summary>
    [Export] public Node? Root { get; set; }

    private ObjectKind _kind = ObjectKind.Auto;

    /// <summary>How authority over this object moves. Read when the object enters the tree.</summary>
    [Export]
    public ObjectKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            NotifyPropertyListChanged();
            UpdateConfigurationWarnings();
        }
    }

    /// <summary><see cref="Kind"/> with <see cref="ObjectKind.Auto"/> resolved from the root's type.</summary>
    public ObjectKind ResolvedKind => Kind == ObjectKind.Auto ? KindFor(Root ?? GetParent()) : Kind;

    private bool _transferable = true;

    /// <summary>
    /// Whether other peers may take authority or ownership. Set by <see cref="Kind"/>; by hand only for
    /// <see cref="ObjectKind.Custom"/>. The current authority sends runtime changes through the host.
    /// </summary>
    [Export]
    public bool Transferable
    {
        get => _transferable;
        set
        {
            if (_transferable == value) return;
            _transferable = value;
            if (!Registered || !IsAuthority) return;
            TransferableSequence++;
            Context.NetworkObjectServer.SubmitAuthority(this);
        }
    }

    /// <summary>Whether this object passes its authority on with <see cref="Touch"/>. Set by <see cref="Kind"/>.</summary>
    [Export] public bool SpreadsAuthority { get; set; }

    /// <summary>Maximum contacts from the source of a spread chain, or -1 for unlimited.</summary>
    [Export(PropertyHint.Range, "-1,64,1")] public int MaxSpreadDepth { get; set; } = -1;

    /// <summary>What this object sends, in the order it is sent. Read-only; shown in the inspector.</summary>
    [Export(PropertyHint.MultilineText)]
    public string SyncedSummary
    {
        get => string.Join("\n", DescribeSynced());
        set { }
    }

    /// <summary>The peer holding the object, or 0 when nobody does.</summary>
    public int Holder { get; private set; }

    internal int AuthoritySequence { get; private set; }
    internal int OwnershipSequence { get; private set; }
    internal int TransferableSequence { get; private set; }

    internal bool Registered { get; set; }
    internal int SpreadDepth { get; private set; }
    internal int SpreadLimit { get; private set; } = -1;
    internal string SpreadCause { get; private set; } = "";
    internal int EffectiveSpreadLimit => SpreadDepth == 0 ? MaxSpreadDepth : SpreadLimit;

    /// <summary>Raised after the authority or the holder changed, on every peer.</summary>
    public event Action? AuthorityChanged;

    /// <summary>Sequences, display tick and sample events: for checks and diagnostics, not for game logic.</summary>
    public ObjectDiagnostics Diagnostics { get; }

    public NetworkObject() => Diagnostics = new ObjectDiagnostics(this);

    internal List<(Node Node, NodePath Property, bool Interpolate)> Properties { get; } = new();
    internal SampleTrack<Sample> Track { get; } = new();
    internal ObjectPlaybackCursor PlaybackCursor { get; } = new();
    internal bool PlaybackStarted { get; set; }

    internal double? DisplayTick { get; set; }
    internal bool TeleportPending { get; set; }
    internal bool DespawnRequested { get; set; }
    internal bool RemoteDespawned { get; set; }

    /// <summary>What this peer last sent for the object, and when: an unchanged object is not sent again for a while.</summary>
    internal byte[]? LastSentBody { get; set; }
    internal int LastSentTick { get; set; }
    internal bool WarnedOversized { get; set; }

    /// <summary>True when this peer simulates the object and sends its state.</summary>
    public bool IsAuthority => Root!.IsMultiplayerAuthority();

    public int Authority => Root!.GetMultiplayerAuthority();

    private int LocalPeer => Root!.Multiplayer.GetUniqueId();

    /// <summary>The object whose root is <paramref name="root"/>, or null when it is not a registered object.</summary>
    public static NetworkObject? Of(Node root) => NetfoxContext.For(root).NetworkObjectServer?.Find(root);

    /// <summary>The next state this peer sends applies without interpolation on the others: a respawn, not a flight.</summary>
    public void Teleport() => TeleportPending = true;

    /// <summary>
    /// Ends this authoritative object's timeline. It is hidden and stops processing here immediately; remote peers
    /// hide it when their playback reaches the flagged final sample, and the root is freed after the playback grace
    /// period so a <see cref="MultiplayerSpawner"/> cannot remove it from observers early.
    /// </summary>
    public bool Despawn()
    {
        if (!IsAuthority || DespawnRequested) return false;
        DespawnRequested = true;
        SetShown(false);
        Root!.ProcessMode = ProcessModeEnum.Disabled;

        var graceTicks = NetworkObjectServer.MaxPlaybackDepthTicks
                         + NetworkObjectServer.StateIntervalTicks * 2;
        var timer = GetTree().CreateTimer(graceTicks / Context.NetworkTime.Tickrate);
        timer.Timeout += () =>
        {
            if (GodotObject.IsInstanceValid(Root)) Root.QueueFree();
        };
        return true;
    }

    /// <summary>
    /// Takes authority over a free object this peer interacts with. Physics bodies do this themselves on contact; call
    /// it for other interactions, or for a <see cref="ObjectKind.Custom"/> object. False when it is held by someone
    /// else, or when this peer is not connected; true means applied here and sent, not yet accepted by the host.
    /// </summary>
    public bool TryTakeAuthority()
    {
        if (!Transferable || (Holder != 0 && Holder != LocalPeer)) return false;
        if (IsAuthority) return true;
        return Request(LocalPeer, Holder, AuthoritySequence + 1, OwnershipSequence, null, 0, -1);
    }

    /// <summary>
    /// Passes this object's authority to <paramref name="other"/> after contact. Physics bodies call it themselves; call
    /// it for contact the physics engine does not report. The source's depth limit follows the whole chain; the host
    /// verifies this object as the cause and arbitrates opposing requests.
    /// </summary>
    public bool Touch(NetworkObject other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!IsAuthority || !SpreadsAuthority || ReferenceEquals(this, other)) return false;
        var nextDepth = SpreadDepth + 1;
        var limit = EffectiveSpreadLimit;
        if (limit >= 0 && nextDepth > limit) return false;
        if (!other.Transferable || other.Holder is not 0 && other.Holder != LocalPeer) return false;
        if (other.IsAuthority) return true;
        return other.Request(LocalPeer, other.Holder, other.AuthoritySequence + 1, other.OwnershipSequence,
            this, nextDepth, limit);
    }

    /// <summary>Takes ownership and authority. False when someone else holds it.</summary>
    public bool TryGrab()
    {
        if (!Transferable || (Holder != 0 && Holder != LocalPeer)) return false;
        if (Holder == LocalPeer) return true;
        return Request(LocalPeer, LocalPeer, AuthoritySequence + 1, OwnershipSequence + 1, null, 0, -1);
    }

    /// <summary>Lets go of a held object. This peer keeps simulating it until someone else touches it.</summary>
    public bool Release()
        => Holder == LocalPeer && Request(LocalPeer, 0, AuthoritySequence, OwnershipSequence + 1, null, 0, -1);

    /// <summary>Lets go of a held object with <paramref name="velocity"/>: the throw flies on this peer's simulation.</summary>
    public bool Throw(Vector3 velocity)
    {
        if (!Release()) return false;
        switch (Root)
        {
            case RigidBody3D body: body.LinearVelocity = velocity; break;
            case RigidBody2D body: body.LinearVelocity = new Vector2(velocity.X, velocity.Y); break;
            case CharacterBody3D body: body.Velocity = velocity; break;
            case CharacterBody2D body: body.Velocity = new Vector2(velocity.X, velocity.Y); break;
        }
        return true;
    }

    /// <summary>
    /// Hands a free object this peer simulates back to the host. A settled <see cref="ObjectKind.Shared"/> physics body
    /// does this itself.
    /// </summary>
    public bool ReturnToHost()
        => IsAuthority && Holder == 0 && LocalPeer != HostPeer
           && Request(HostPeer, 0, AuthoritySequence + 1, OwnershipSequence, null, 0, -1);

    /// <summary>
    /// Raised on the authority, exactly once per <see cref="Send"/> call anywhere: the peer that sent it and what it
    /// sent.
    /// </summary>
    public event Action<int, Variant>? Received;

    /// <summary>
    /// Raised on the authority of a root that is not a rigid body, exactly once per <see cref="Knock"/>: the impulse, for
    /// the game to apply as knockback. A rigid body takes the impulse itself.
    /// </summary>
    public event Action<Vector3>? Knocked;

    /// <summary>
    /// Delivers <paramref name="payload"/> to whoever is this object's authority, reliably and exactly once, even if
    /// authority moves while it is on its way. On the authority itself it is raised at once.
    /// </summary>
    public void Send(Variant payload) => Deliver(LocalPeer, EventKind.User, payload, hops: 0);

    /// <summary>
    /// Pushes this object from wherever the caller is: its authority applies <paramref name="impulse"/> to a rigid body
    /// (X and Y for 2D) or raises <see cref="Knocked"/>. Delivered like <see cref="Send"/>.
    /// </summary>
    public void Knock(Vector3 impulse) => Deliver(LocalPeer, EventKind.Knock, impulse, hops: 0);

    internal enum EventKind { User = 0, Knock = 1 }

    /// <summary>
    /// Raises an event here if this peer is the authority, and passes it on otherwise. While this peer's own request
    /// is unanswered its authority may be about to be taken back, so the event waits for the host's answer.
    /// </summary>
    internal void Deliver(int origin, EventKind kind, Variant payload, int hops)
    {
        if (!IsAuthority) Context.NetworkObjectServer.SendEvent(this, Authority, origin, kind, payload, hops);
        else if (PendingRequest != 0) _heldEvents.Add((origin, kind, payload, hops));
        else Raise(origin, kind, payload);
    }

    private void Raise(int origin, EventKind kind, Variant payload)
    {
        if (kind == EventKind.User)
        {
            Received?.Invoke(origin, payload);
            return;
        }

        var impulse = payload.AsVector3();
        switch (Root)
        {
            case RigidBody3D body: body.ApplyCentralImpulse(impulse); break;
            case RigidBody2D body: body.ApplyCentralImpulse(new Vector2(impulse.X, impulse.Y)); break;
            default: Knocked?.Invoke(impulse); break;
        }
    }

    private readonly List<(int Origin, EventKind Kind, Variant Payload, int Hops)> _heldEvents = new();

    /// <summary>The id of this guest's latest authority request the host has not answered yet, or 0.</summary>
    internal int PendingRequest { get; private set; }

    private int _lastRequestId;

    internal int NextRequest() => PendingRequest = ++_lastRequestId;

    /// <summary>The host answered <paramref name="requestId"/>: events held for it go wherever authority now is.</summary>
    internal void Answered(int requestId)
    {
        if (requestId == 0 || requestId != PendingRequest) return;
        PendingRequest = 0;
        var held = _heldEvents.ToArray();
        _heldEvents.Clear();
        foreach (var (origin, kind, payload, hops) in held) Deliver(origin, kind, payload, hops);
    }

    internal const int HostPeer = 1;

    private bool Request(
        int authority,
        int owner,
        int authoritySequence,
        int ownershipSequence,
        NetworkObject? cause,
        int spreadDepth,
        int spreadLimit)
    {
        // A change nobody hears about would leave this peer disagreeing with everyone for good
        if (Root!.Multiplayer.MultiplayerPeer is not { } peer || peer is OfflineMultiplayerPeer
            || peer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Connected)
            return false;

        var causeName = cause is null
            ? ""
            : Context.NetworkIdentityServer.GetIdentifierOf(cause.Root!)?.FullName;
        if (cause is not null && causeName is null) return false;

        Apply(authority, owner, authoritySequence, ownershipSequence, _transferable, TransferableSequence,
            causeName ?? "", spreadDepth, spreadLimit);
        Context.NetworkObjectServer.SubmitAuthority(this);
        return true;
    }

    /// <summary>True when (<paramref name="ownershipSequence"/>, <paramref name="authoritySequence"/>) is newer than what this object has.</summary>
    internal bool IsNewer(int authoritySequence, int ownershipSequence)
        => ownershipSequence > OwnershipSequence
           || (ownershipSequence == OwnershipSequence && authoritySequence > AuthoritySequence);

    internal void Apply(
        int authority,
        int owner,
        int authoritySequence,
        int ownershipSequence,
        bool transferable,
        int transferableSequence,
        string spreadCause = "",
        int spreadDepth = 0,
        int spreadLimit = -1)
    {
        var changed = authority != Authority || owner != Holder;
        if (authority != Authority)
        {
            SetAuthority(Root!, authority);
            if (IsAuthority && !Shown) SetShown(true);
            // Samples are stamped on the previous authority's clock; the new one sends its own, at once even at rest
            Track.Clear();
            PlaybackCursor.Reset();
            PlaybackStarted = false;
            DisplayTick = null;
            RemoteDespawned = false;
            LastSentBody = null;
        }

        Holder = owner;
        AuthoritySequence = authoritySequence;
        OwnershipSequence = ownershipSequence;
        if (transferableSequence >= TransferableSequence)
        {
            _transferable = transferable;
            TransferableSequence = transferableSequence;
        }
        SpreadCause = spreadCause;
        SpreadDepth = spreadDepth;
        SpreadLimit = spreadLimit;
        if (!changed) return;
        _body?.AuthorityChanged();
        AuthorityChanged?.Invoke();
    }

    private static void SetAuthority(Node node, int peer)
    {
        node.SetMultiplayerAuthority(peer, recursive: false);
        foreach (var child in node.GetChildren())
            if (!HasOwnObject(child, except: null) || child is NetworkObject)
                SetAuthority(child, peer);
    }

    /// <summary>What <see cref="ObjectKind.Auto"/> resolves to for a root of this type.</summary>
    public static ObjectKind KindFor(Node? root) => root switch
    {
        CharacterBody3D or CharacterBody2D => ObjectKind.Personal,
        RigidBody3D or RigidBody2D => ObjectKind.Shared,
        PhysicsBody3D or PhysicsBody2D or Area3D or Area2D => ObjectKind.World,
        Node3D or Node2D => ObjectKind.Personal,
        _ => ObjectKind.World,
    };

    /// <summary>Why a root of this type cannot be replicated, or null when it can.</summary>
    public static string? UnsupportedReason(Node? root) => root switch
    {
        SoftBody3D => "SoftBody3D is not supported: its vertices are not replicated",
        PhysicalBone3D or PhysicalBone2D => "a ragdoll bone is not supported: replicate the character that owns it",
        _ => null,
    };

    /// <summary>What is sent for a root of this type before its <c>[Synced]</c> properties.</summary>
    internal static string[] AutoProperties(Node? root) => root switch
    {
        RigidBody3D or RigidBody2D => ["global_transform", "linear_velocity", "angular_velocity"],
        CharacterBody3D or CharacterBody2D => ["global_transform", "velocity"],
        Node3D or Node2D => ["global_transform"],
        _ => [],
    };

    private void ApplyKind()
    {
        switch (ResolvedKind)
        {
            case ObjectKind.Personal:
                _transferable = false;
                SpreadsAuthority = true;
                break;
            case ObjectKind.Shared:
                _transferable = true;
                SpreadsAuthority = true;
                break;
            case ObjectKind.World:
                _transferable = false;
                SpreadsAuthority = false;
                break;
        }
    }

    public override string[] _GetConfigurationWarnings()
        => UnsupportedReason(Root ?? GetParent()) is { } reason && Kind != ObjectKind.Custom
            ? [$"{reason}. The game will not start with this object; set Kind to Custom to replicate it by hand."]
            : [];

    public override void _ValidateProperty(Dictionary property)
    {
        var name = property["name"].AsStringName();
        if (name == PropertyName.SyncedSummary)
            property["usage"] = (int)(PropertyUsageFlags.Editor | PropertyUsageFlags.ReadOnly);
        else if ((name == PropertyName.Transferable || name == PropertyName.SpreadsAuthority) && Kind != ObjectKind.Custom)
            property["usage"] = (int)PropertyUsageFlags.NoEditor;
    }

    public override void _EnterTree()
    {
        if (Engine.IsEditorHint()) return;
        Context = NetfoxContext.For(this);
        Root ??= GetParent();
        ApplyKind();
    }

    public override void _Ready()
    {
        if (Engine.IsEditorHint()) return;
        if (UnsupportedReason(Root) is { } reason && Kind != ObjectKind.Custom)
        {
            Logger.Error("{0}: {1}. Set Kind to Custom to replicate it by hand", Root!.GetPath(), reason);
            GetTree().Quit(1);
            return;
        }

        Gather(Root!);
        // Nothing to show until playback reaches this object's first sample: a projectile would otherwise hang at the
        // muzzle for the playback delay before it flies
        if (!IsAuthority) SetShown(false);
        if (ResolvedKind != ObjectKind.Custom) _body = PhysicsHandling.For(this);
        Context.NetworkObjectServer.Register(this);
        _body?.AuthorityChanged();
    }

    private PhysicsHandling? _body;

    public override void _PhysicsProcess(double delta)
    {
        if (Engine.IsEditorHint()) return;
        _body?.PhysicsProcess();
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

    public override void _ExitTree()
    {
        if (Engine.IsEditorHint()) return;
        Context.NetworkObjectServer?.Deregister(this);
    }

    private void Gather(Node root)
    {
        foreach (var property in AutoProperties(root))
            Properties.Add((root, new NodePath(property), true));
        GatherSynced(root);
    }

    private void GatherSynced(Node node)
    {
        if (node is ISyncedProperties synced)
            foreach (var property in synced.GetSyncedProperties())
                Properties.Add((node, new NodePath(property.Path), property.Interpolate));

        foreach (var child in node.GetChildren())
        {
            if (child is NetworkObject || HasOwnObject(child, except: this)) continue;
            GatherSynced(child);
        }
    }

    /// <summary>
    /// The inspector's list. In the editor a script without <c>[Tool]</c> is a placeholder, so its <c>[Synced]</c>
    /// properties are read from the compiled type the script path points at.
    /// </summary>
    private IEnumerable<string> DescribeSynced()
    {
        if (Properties.Count > 0)
            return Properties.Select(entry => entry.Node == Root ? $"{entry.Property}" : $"{Root!.GetPathTo(entry.Node)}:{entry.Property}");

        var root = Root ?? GetParent();
        if (root is null) return [];
        if (UnsupportedReason(root) is { } reason) return [reason];
        var lines = AutoProperties(root).ToList();
        DescribeSyncedIn(root, root, lines);
        return lines;
    }

    private void DescribeSyncedIn(Node root, Node node, List<string> lines)
    {
        if (node.GetScript().AsGodotObject() is Script script && TypeOfScript(script.ResourcePath) is { } type)
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (property.GetCustomAttributes().Any(attribute => attribute.GetType().Name == "SyncedAttribute"))
                    lines.Add(node == root ? property.Name : $"{root.GetPathTo(node)}:{property.Name}");

        foreach (var child in node.GetChildren())
        {
            if (child is NetworkObject || HasOwnObject(child, except: this)) continue;
            DescribeSyncedIn(root, child, lines);
        }
    }

    private static Type? TypeOfScript(string path)
        => AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(assembly =>
            {
                try
                {
                    return assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    return e.Types.OfType<Type>().ToArray();
                }
            })
            .FirstOrDefault(type => type.GetCustomAttribute<ScriptPathAttribute>()?.Path == path);

    private static bool HasOwnObject(Node node, NetworkObject? except)
    {
        foreach (var child in node.GetChildren())
            if (child is NetworkObject other && other != except) return true;
        return false;
    }

    internal sealed class Sample(Variant[] values, bool teleport, bool despawned)
    {
        public Variant[] Values { get; } = values;
        public bool Teleport { get; } = teleport;
        public bool Despawned { get; } = despawned;
    }

    /// <summary>What a <see cref="NetworkObject"/> exposes for checks and diagnostics rather than for game logic.</summary>
    public sealed class ObjectDiagnostics
    {
        private readonly NetworkObject _object;

        internal ObjectDiagnostics(NetworkObject obj) => _object = obj;

        public int AuthoritySequence => _object.AuthoritySequence;
        public int OwnershipSequence => _object.OwnershipSequence;
        public int TransferableSequence => _object.TransferableSequence;

        /// <summary>The tick this object was last displayed at on a remote peer, including its opening catch-up.</summary>
        public double? DisplayTick => _object.DisplayTick;

        /// <summary>
        /// Raised on the authority for every state it sends: the tick. Not every tick - an unchanged object is sent
        /// only as a heartbeat - so this is the ground truth a check compares playback against.
        /// </summary>
        public event Action<int>? SampleSent;

        /// <summary>Raised on a peer playing the object back, for every sample of the authority's state it kept.</summary>
        public event Action<int>? SampleReceived;

        internal void RaiseSampleSent(int tick) => SampleSent?.Invoke(tick);
        internal void RaiseSampleReceived(int tick) => SampleReceived?.Invoke(tick);
    }
}
