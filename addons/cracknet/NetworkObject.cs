using System.Reflection;
using CrackNet.Core.Logging;
using CrackNet.Core.Time;
using CrackNet.Internal;
using Godot;
using Godot.Collections;

namespace CrackNet;

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
    private static readonly CrackNetLogger Logger = CrackNetLogger.ForCrackNet("NetworkObject");

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
    public CrackNetContext Context { get; private set; } = CrackNetContext.Default;

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

    /// <summary>Whether this object passes its authority on with <see cref="Spread"/>. Set by <see cref="Kind"/>.</summary>
    [Export] public bool SpreadsAuthority { get; set; }

    /// <summary>Maximum contacts from the source of a spread chain, or -1 for unlimited.</summary>
    [Export(PropertyHint.Range, "-1,64,1")] public int MaxSpreadDepth { get; set; } = -1;

    /// <summary>
    /// The node everything drawn for this object sits under: an empty <c>Node3D</c> under the root, with the model
    /// inside it. When the object changes hands it is drawn where it was on screen and catches up with the body over
    /// <see cref="SmoothingTime"/>, instead of jumping; the body itself moves at once. Empty: no smoothing.
    /// </summary>
    [ExportGroup("Authority Change Smoothing")]
    [Export]
    public Node3D? Visual
    {
        get => _visual;
        set
        {
            _visual = value;
            UpdateConfigurationWarnings();
        }
    }

    private Node3D? _visual;

    /// <summary>What is wrong with <see cref="Visual"/>, or null: it has to be under the root, never the root itself.</summary>
    internal static string? VisualProblem(Node? root, Node3D? visual)
        => visual is null || root is not Node3D || visual != root && root.IsAncestorOf(visual)
            ? null
            : "Visual has to be a node under the object's root, not the root itself: moving it would move the body. "
              + "Put the model under an empty Node3D and point Visual at that";

    /// <summary>How long the drawing takes to catch up with the body after the object changed hands.</summary>
    [Export(PropertyHint.Range, "0,1,0.01,suffix:s")] public float SmoothingTime { get; set; } = 0.15f;

    /// <summary>A handover that moves the object further than this is drawn at once: it is a move, not a lag.</summary>
    [Export(PropertyHint.Range, "0,20,0.1,or_greater,suffix:m")] public float MaxSmoothingDistance { get; set; } = 2;

    /// <summary>What this object sends, in the order it is sent. Read-only; shown in the inspector.</summary>
    [ExportGroup("")]
    [Export(PropertyHint.MultilineText)]
    public string SyncedSummary
    {
        get => string.Join("\n", DescribeSynced());
        set { }
    }

    /// <summary>The peer holding the object, or 0 when nobody does.</summary>
    public int ClaimedBy { get; private set; }

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

    public NetworkObject()
    {
        Diagnostics = new ObjectDiagnostics(this);
        Authority = new ObjectAuthority(this);
    }

    internal List<(Node Node, NodePath Property, bool Interpolate)> Properties { get; } = new();
    internal SampleTrack<Sample> Track { get; } = new();
    internal ObjectPlaybackCursor PlaybackCursor { get; } = new();
    internal bool PlaybackStarted { get; set; }

    internal double? DisplayTick { get; set; }
    internal bool SnapPending { get; set; }

    /// <summary>State from a peer that is not the authority here yet, kept for when the host's word arrives.</summary>
    internal List<(int Sender, int Tick, byte[] Body, ulong ReceivedAt)> EarlySamples { get; } = new();
    internal bool DespawnRequested { get; set; }
    internal bool RemoteDespawned { get; set; }

    /// <summary>What this peer last sent for the object, and when: an unchanged object is not sent again for a while.</summary>
    internal byte[]? LastSentBody { get; set; }
    internal int LastSentTick { get; set; }
    internal bool WarnedOversized { get; set; }

    /// <summary>Physics frames a simulated body has been at rest, counted by its physics handling.</summary>
    internal int RestFrames { get; set; }

    /// <summary>Who simulates the object and sends its state, and taking or returning that by hand.</summary>
    public ObjectAuthority Authority { get; }

    internal bool IsAuthority => Root!.IsMultiplayerAuthority();

    internal int AuthorityPeer => Root!.GetMultiplayerAuthority();

    private int LocalPeer => Root!.Multiplayer.GetUniqueId();

    /// <summary>The object whose root is <paramref name="root"/>, or null when it is not a registered object.</summary>
    public static NetworkObject? Of(Node root) => CrackNetContext.For(root).NetworkObjectServer?.Find(root);

    /// <summary>The next state this peer sends applies without interpolation on the others: a respawn, not a flight.</summary>
    public void Snap()
    {
        SnapPending = true;
        _smoothing?.Snapped();
    }

    /// <summary>A snap sample was applied here: it is to be seen, not smoothed.</summary>
    internal void SnapApplied() => _smoothing?.Snapped();

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

        var graceTicks = Context.NetworkObjectServer.MaxPlaybackDepthTicks
                         + Context.NetworkObjectServer.StateIntervalTicks * 2;
        var timer = GetTree().CreateTimer(graceTicks / Context.NetworkTime.Tickrate);
        timer.Timeout += () =>
        {
            if (GodotObject.IsInstanceValid(Root)) Root.QueueFree();
        };
        return true;
    }

    internal bool TryTakeAuthority()
    {
        if (!Transferable || (ClaimedBy != 0 && ClaimedBy != LocalPeer)) return false;
        if (IsAuthority) return true;
        return Request(LocalPeer, ClaimedBy, AuthoritySequence + 1, OwnershipSequence, null, 0, -1);
    }

    /// <summary>
    /// Passes this object's authority to <paramref name="other"/> after contact. Physics bodies call it themselves; call
    /// it for contact the physics engine does not report. The source's depth limit follows the whole chain; the host
    /// verifies this object as the cause and arbitrates opposing requests.
    /// </summary>
    public bool Spread(NetworkObject other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!IsAuthority || !SpreadsAuthority || ReferenceEquals(this, other)) return false;
        var nextDepth = SpreadDepth + 1;
        var limit = EffectiveSpreadLimit;
        if (limit >= 0 && nextDepth > limit) return false;
        if (!other.Transferable || other.ClaimedBy is not 0 && other.ClaimedBy != LocalPeer) return false;
        if (other.IsAuthority) return true;
        return other.Request(LocalPeer, other.ClaimedBy, other.AuthoritySequence + 1, other.OwnershipSequence,
            this, nextDepth, limit);
    }

    /// <summary>
    /// Optimistically makes the object this peer's: authority and ownership, so nobody else can take it until it is
    /// released. It applies here at once and the host is asked; two peers grabbing within a ping both see it in hand
    /// until the host's answer takes it from one of them, through <see cref="IAuthorityChanged"/>. So drive game logic
    /// from <see cref="ClaimedBy"/> rather than from the return value. A physics body is frozen while claimed; the
    /// game moves it. False only when refusal is known here: someone else holds it, it is not transferable, or this
    /// peer is not connected.
    /// </summary>
    public bool TryClaim()
    {
        if (!Transferable || (ClaimedBy != 0 && ClaimedBy != LocalPeer)) return false;
        if (ClaimedBy == LocalPeer) return true;
        return Request(LocalPeer, LocalPeer, AuthoritySequence + 1, OwnershipSequence + 1, null, 0, -1);
    }

    /// <summary>Lets go of a held object. This peer keeps simulating it until someone else touches it.</summary>
    public bool ReleaseClaim()
        => ClaimedBy == LocalPeer && Request(LocalPeer, 0, AuthoritySequence, OwnershipSequence + 1, null, 0, -1);

    /// <summary>Lets go of a held object with <paramref name="velocity"/>: the throw flies on this peer's simulation.</summary>
    public bool ReleaseClaim(Vector3 velocity)
    {
        if (!ReleaseClaim()) return false;
        switch (Root)
        {
            case RigidBody3D body: body.LinearVelocity = velocity; break;
            case CharacterBody3D body: body.Velocity = velocity; break;
        }
        return true;
    }

    /// <summary>
    /// Optimistically hangs <paramref name="item"/> on <paramref name="anchor"/>, a node under this object's root (a
    /// <c>Marker3D</c>, under a <c>BoneAttachment3D</c> for a bone). While attached the item sends no transform: its
    /// samples name the carrier and the anchor, and every peer, this one included, puts it on its own copy of the anchor
    /// after that peer's animation, so a hand and what it holds cannot drift apart. The item is claimed, so nobody
    /// else can take it, and its collisions are off. Other peers show the change when their playback of the item
    /// reaches it. False only when refusal is known here: the anchor is not under this root (an error), the item is
    /// attached already, it would carry its own carrier, or it cannot be claimed; drive game logic from
    /// <see cref="Attached"/> and <see cref="AttachedTo"/>, as with <see cref="TryClaim"/>.
    /// </summary>
    public bool TryAttach(NetworkObject item, Node3D anchor)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(anchor);
        if (Root is not Node3D root || !root.IsAncestorOf(anchor))
        {
            Logger.Error("{0}: the anchor {1} is not under this object's root, so its path cannot be sent. Put the anchor under {2}",
                GetPath(), anchor.GetPath(), Root?.GetPath());
            return false;
        }
        if (ReferenceEquals(item, this) || item._carrier is not null) return false;
        for (var above = _carrier; above is not null; above = above._carrier)
            if (ReferenceEquals(above, item)) return false;   // it would carry its own carrier
        // ponytail: a player (not transferable) is carried in a later step of #70; until then only what can be claimed
        if (!item.Transferable || !item.TryClaim()) return false;
        item.Hang(this, anchor, Transform3D.Identity);
        return true;
    }

    /// <summary>
    /// Takes <paramref name="item"/> off this object and lets go of it: from here on it sends its own transform again,
    /// and a physics body falls or flies. To throw it, <c>Detach</c> and then <see cref="Impulse(Vector3)"/> it. Other
    /// peers show the change when their playback reaches it and draw the item catching up from the hand to where the
    /// thrower has it (see <see cref="Visual"/>). False when the item is not attached here or not simulated here.
    /// </summary>
    public bool Detach(NetworkObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!ReferenceEquals(item._carrier, this) || !item.IsAuthority) return false;
        item.Unhang();
        if (item.ClaimedBy == LocalPeer) item.ReleaseClaim();
        return true;
    }

    /// <summary>The roots of the items hanging on this object, as this peer shows them.</summary>
    public IEnumerable<Node> Attached => _attached.Select(item => item.Root!);

    /// <summary>The root of the object this one hangs on, or null, as this peer shows it.</summary>
    public Node? AttachedTo => _carrier?.Root;

    /// <summary>Raised after this object's attachments, or its own attachment, changed here; for watching another object.</summary>
    public event Action? AttachmentChanged;

    /// <summary>What an attached item sends instead of a transform: its carrier by full name and the anchor's path under the carrier's root.</summary>
    internal sealed record Attachment(string Carrier, string Anchor);

    /// <summary>What this item sends about its attachment, or null when it is free.</summary>
    internal Attachment? AttachmentState { get; private set; }

    internal NetworkObject? Carrier => _carrier;

    private readonly List<NetworkObject> _attached = new();
    private NetworkObject? _carrier;
    private Node3D? _anchor;
    /// <summary>How the item sits on the anchor: identity when hung here, whatever the authority sends otherwise.</summary>
    private Transform3D _anchorOffset = Transform3D.Identity;
    private (uint Layer, uint Mask)? _collisionsBeforeAttach;
    private string? _unresolvedAttachment;

    /// <summary>Hangs this object on <paramref name="anchor"/> of <paramref name="carrier"/> on this peer: by the authority, or by playback.</summary>
    internal void Hang(NetworkObject carrier, Node3D anchor, Transform3D offset)
    {
        _anchorOffset = offset;
        if (ReferenceEquals(_carrier, carrier) && ReferenceEquals(_anchor, anchor)) return;
        _carrier?._attached.Remove(this);
        _carrier = carrier;
        _anchor = anchor;
        carrier._attached.Add(this);
        var carrierName = Context.NetworkIdentityServer.GetIdentifierOf(carrier.Root!)?.FullName ?? "";
        AttachmentState = new Attachment(carrierName, carrier.Root!.GetPathTo(anchor).ToString());
        // Moved by hand into the hand's position every frame, a colliding body lands inside whatever is there and the
        // physics engine throws that out of the world: it passes through things while attached, on every peer
        if (_collisionsBeforeAttach is null && Root is CollisionObject3D body)
        {
            _collisionsBeforeAttach = (body.CollisionLayer, body.CollisionMask);
            body.CollisionLayer = 0;
            body.CollisionMask = 0;
        }
        Place();
        NotifyAttachmentChanged(carrier);
    }

    /// <summary>Takes this object off its carrier on this peer.</summary>
    internal void Unhang()
    {
        if (_carrier is not { } carrier) return;
        carrier._attached.Remove(this);
        _carrier = null;
        _anchor = null;
        AttachmentState = null;
        _unresolvedAttachment = null;
        if (_collisionsBeforeAttach is { } collisions && Root is CollisionObject3D body)
        {
            body.CollisionLayer = collisions.Layer;
            body.CollisionMask = collisions.Mask;
        }
        _collisionsBeforeAttach = null;
        // Where the item goes next is a jump from the hand: on an observer the thrower's first free sample is a
        // playback delay ahead of its hand. Drawn catching up, as a handover is
        _smoothing?.Opened();
        NotifyAttachmentChanged(carrier);
    }

    private void NotifyAttachmentChanged(NetworkObject carrier)
    {
        if (Root is IAttachmentChanged item) item.OnAttachmentChanged();
        if (carrier.Root is IAttachmentChanged root) root.OnAttachmentChanged();
        AttachmentChanged?.Invoke();
        carrier.AttachmentChanged?.Invoke();
    }

    /// <summary>Puts this attached item on its anchor, where the anchor is now. Once per frame after the animation, and at once when hung.</summary>
    internal void Place()
    {
        if (_anchor is null || Root is not Node3D node || !_anchor.IsInsideTree() || !node.IsInsideTree()) return;
        node.GlobalTransform = _anchor.GlobalTransform * _anchorOffset;
        // The hand's motion is not a jump to smooth: the smoothing follows the body while it hangs
        _smoothing?.Following();
    }

    /// <summary>The value property <paramref name="index"/> sends: the anchor offset instead of the transform while attached.</summary>
    internal Variant ValueToSend(int index)
        => index == 0 && _carrier is not null && Root is Node3D ? _anchorOffset : Properties[index].Node.GetValue(Properties[index].Property);

    /// <summary>Playback reached a sample that hangs this item on <paramref name="attachment"/>: shown on this peer's own copy of the carrier.</summary>
    internal void ShowAttached(Attachment attachment, Transform3D offset)
    {
        if (attachment == AttachmentState)
        {
            _anchorOffset = offset;
            return;
        }
        var identifier = Context.NetworkIdentityServer.ResolveReference(AuthorityPeer, Core.Data.NetworkIdentityReference.OfFullName(attachment.Carrier), allowQueue: false);
        var carrier = identifier is null ? null : Of(identifier.Subject);
        var anchor = carrier?.Root is Node3D root ? root.GetNodeOrNull<Node3D>(attachment.Anchor) : null;
        if (carrier is null || anchor is null)
        {
            // Not here yet (a late joiner still receiving the scene), or a scene that differs from the authority's
            if (_unresolvedAttachment != attachment.Carrier)
                Logger.Warning("{0} is attached to {1} at {2}, which this peer does not have; it stays where it is", Root!.Name, attachment.Carrier, attachment.Anchor);
            _unresolvedAttachment = attachment.Carrier;
            return;
        }
        Hang(carrier, anchor, offset);
    }

    /// <summary>Playback reached a free sample: off the carrier, if playback had hung it.</summary>
    internal void ShowFree() => Unhang();

    internal bool ReturnToHost()
        => IsAuthority && ClaimedBy == 0 && LocalPeer != HostPeer
           && Request(HostPeer, 0, AuthoritySequence + 1, OwnershipSequence, null, 0, -1);

    /// <summary>
    /// Raised on the authority, exactly once per <see cref="Send"/> call anywhere: the peer that sent it and what it
    /// sent.
    /// </summary>
    public event Action<int, Variant>? Received;

    /// <summary>
    /// Raised on the authority of a root that is not a rigid body, exactly once per push: the impulse, for watching
    /// another object. The node itself implements <see cref="IImpulsed"/>. The push is also added to
    /// <see cref="ImpulseVelocity"/>. A rigid body takes the impulse itself.
    /// </summary>
    public event Action<Vector3>? Impulsed;

    /// <summary>
    /// How hard a character body pushes the rigid bodies it slides into, along the contact normal; 0 is off. The library
    /// takes the body and pushes it on this peer's simulation.
    /// </summary>
    [Export(PropertyHint.Range, "0,20,0.05,or_greater")] public float ImpulseStrength { get; set; }

    /// <summary>How fast <see cref="ImpulseVelocity"/> fades, in metres per second per second.</summary>
    [Export(PropertyHint.Range, "0,100,0.5,or_greater")] public float ImpulseDecay { get; set; } = 20;

    /// <summary>
    /// The velocity the pushes received give a root that is not a rigid body, fading by <see cref="ImpulseDecay"/>
    /// each physics frame. A character adds it where it composes its <c>Velocity</c>, next to gravity, every frame:
    /// a controller writes its horizontal velocity from input each frame, so a push added once would last one frame.
    /// </summary>
    public Vector3 ImpulseVelocity { get; private set; }

    /// <summary>
    /// Delivers <paramref name="payload"/> to whoever is this object's authority, reliably and exactly once, even if
    /// authority moves while it is on its way. On the authority itself it is raised at once.
    /// </summary>
    public void Send(Variant payload) => Deliver(LocalPeer, EventKind.User, payload, hops: 0);

    /// <summary>
    /// Pushes this object with nothing doing the pushing - an explosion, a trap: its authority applies
    /// <paramref name="impulse"/> to a rigid body or raises <see cref="Impulsed"/>. Delivered like
    /// <see cref="Send"/>.
    /// </summary>
    public void Impulse(Vector3 impulse) => Deliver(LocalPeer, EventKind.Impulse, impulse, hops: 0);

    /// <summary>
    /// This object struck <paramref name="target"/>: takes the target when it can (<see cref="Spread"/>), so a crate flies
    /// on this peer's simulation at once, then pushes it. A player, which cannot be taken, is pushed on its own peer. If
    /// the host gives the target to someone else, the winner's simulation stands and the push is passed on to it, so
    /// two players striking the same crate at once both count.
    /// </summary>
    public void Impulse(NetworkObject target, Vector3 impulse)
    {
        ArgumentNullException.ThrowIfNull(target);
        Spread(target);
        if (!target.IsAuthority)
        {
            target.Impulse(impulse);
            return;
        }

        target.Raise(LocalPeer, EventKind.Impulse, impulse);
        // Taken here before the host answered: if the host gives it to someone else, this push goes after it
        if (target.PendingRequest != 0) target._unconfirmedImpulses.Add((LocalPeer, impulse));
    }

    private readonly List<(int Origin, Vector3 Impulse)> _unconfirmedImpulses = new();

    internal enum EventKind { User = 0, Impulse = 1 }

    /// <summary>
    /// Raises an event here if this peer is the authority, and passes it on otherwise. While this peer's own request
    /// is unanswered its authority may be about to be taken back, so the event waits for the host's answer.
    /// </summary>
    internal void Deliver(int origin, EventKind kind, Variant payload, int hops)
    {
        if (!IsAuthority) Context.NetworkObjectServer.SendEvent(this, AuthorityPeer, origin, kind, payload, hops);
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
            default:
                ImpulseVelocity += impulse;
                // The node's own hook first, as with authority changes
                if (Root is IImpulsed root) root.OnImpulsed(impulse);
                Impulsed?.Invoke(impulse);
                break;
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
        // Applied on a simulation the host has just thrown away: the winner applies them on its own instead
        var unconfirmed = _unconfirmedImpulses.ToArray();
        _unconfirmedImpulses.Clear();
        if (!IsAuthority)
            foreach (var (origin, impulse) in unconfirmed) Deliver(origin, EventKind.Impulse, impulse, hops: 0);
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

        Logger.Debug("AUTH request {0}: authority {1} holder {2} seq {3}/{4} cause '{5}' depth {6} (was authority {7} seq {8}/{9})",
            Root.Name, authority, owner, ownershipSequence, authoritySequence, causeName ?? "", spreadDepth,
            AuthorityPeer, OwnershipSequence, AuthoritySequence);
        var changed = Apply(authority, owner, authoritySequence, ownershipSequence, _transferable, TransferableSequence,
            causeName ?? "", spreadDepth, spreadLimit, notify: false);
        // Sent before anyone hears of the change: a body taken here takes what rests on it, and those requests name
        // this one as their cause, so the host has to receive this one first or it refuses them
        Context.NetworkObjectServer.SubmitAuthority(this);
        if (changed) NotifyAuthorityChanged();
        return true;
    }

    /// <summary>True when (<paramref name="ownershipSequence"/>, <paramref name="authoritySequence"/>) is newer than what this object has.</summary>
    internal bool IsNewer(int authoritySequence, int ownershipSequence)
        => ownershipSequence > OwnershipSequence
           || (ownershipSequence == OwnershipSequence && authoritySequence > AuthoritySequence);

    /// <returns>Whether the authority or the holder changed.</returns>
    internal bool Apply(
        int authority,
        int owner,
        int authoritySequence,
        int ownershipSequence,
        bool transferable,
        int transferableSequence,
        string spreadCause = "",
        int spreadDepth = 0,
        int spreadLimit = -1,
        bool notify = true)
    {
        var changed = authority != AuthorityPeer || owner != ClaimedBy;
        if (authority != AuthorityPeer)
        {
            SetAuthority(Root!, authority);
            _smoothing?.Opened();
            if (IsAuthority && !Shown) SetShown(true);
            // Taken here: simulate on from the freshest state heard, not from the one displayed a playback delay ago.
            // Every other peer is already showing the old authority close to that, so the handover does not jump back
            if (IsAuthority && Track.TryGetNewest(out _, out var newest)) Jump(newest);
            // Samples are stamped on the previous authority's clock; the new one sends its own, at once even at rest
            // Whatever hung it was the previous authority's doing: the new one's samples say whether it still hangs
            Unhang();
            Track.Clear();
            PlaybackCursor.Reset();
            PlaybackStarted = false;
            DisplayTick = null;
            RemoteDespawned = false;
            LastSentBody = null;
            Context.NetworkObjectServer.ReplayEarlySamples(this);
        }

        ClaimedBy = owner;
        // Let go of, or taken from this peer's hand by the host's word: an item hangs only while it is held
        if (_carrier is not null && IsAuthority && Transferable && ClaimedBy != LocalPeer) Unhang();
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
        if (changed && notify) NotifyAuthorityChanged();
        return changed;
    }

    private void NotifyAuthorityChanged()
    {
        _body?.AuthorityChanged();
        // The node's own hook first: a game reacts to what it now owns before anyone watching it from outside does
        if (Root is IAuthorityChanged root) root.OnAuthorityChanged();
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
        CharacterBody3D => ObjectKind.Personal,
        RigidBody3D => ObjectKind.Shared,
        PhysicsBody3D or Area3D => ObjectKind.World,
        Node3D => ObjectKind.Personal,
        _ => ObjectKind.World,
    };

    /// <summary>Why a root of this type cannot be replicated, or null when it can.</summary>
    public static string? UnsupportedReason(Node? root) => root switch
    {
        SoftBody3D => "SoftBody3D is not supported: its vertices are not replicated",
        PhysicalBone3D => "a ragdoll bone is not supported: replicate the character that owns it",
        Node2D => "2D is not supported: CrackNet replicates 3D scenes only",
        _ => null,
    };

    /// <summary>What is sent for a root of this type before its <c>[Synced]</c> properties.</summary>
    internal static string[] AutoProperties(Node? root) => root switch
    {
        RigidBody3D => ["global_transform", "linear_velocity", "angular_velocity"],
        CharacterBody3D => ["global_transform", "velocity"],
        Node3D => ["global_transform"],
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
    {
        var warnings = new List<string>();
        if (UnsupportedReason(Root ?? GetParent()) is { } reason && Kind != ObjectKind.Custom)
            warnings.Add($"{reason}. The game will not start with this object; set Kind to Custom to replicate it by hand.");
        if (VisualProblem(Root ?? GetParent(), Visual) is { } visual) warnings.Add(visual + ". Smoothing is off.");
        return warnings.ToArray();
    }

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
        Context = CrackNetContext.For(this);
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
        _smoothing = AuthorityChangeSmoothing.For(this);
        Context.NetworkObjectServer.Register(this);
        // The node learns who has it before its first frame, but after its own _Ready: a child is ready first
        if (Root!.IsNodeReady()) NotifyAuthorityChanged();
        else Root.Connect(Node.SignalName.Ready, Callable.From(NotifyAuthorityChanged), (uint)ConnectFlags.OneShot);
    }

    private PhysicsHandling? _body;
    private AuthorityChangeSmoothing? _smoothing;

    // After the server's own _Process, which places remote bodies: it is an autoload, earlier in the tree
    public override void _Process(double delta)
    {
        if (Engine.IsEditorHint()) return;
        if (_carrier is null) _smoothing?.Process(delta);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (Engine.IsEditorHint()) return;
        _body?.PhysicsProcess();
        // After the root's own step, which is where the game read it: a child processes after its parent
        ImpulseVelocity = ImpulseVelocity.MoveToward(Vector3.Zero, ImpulseDecay * (float)delta);
    }

    /// <summary>
    /// Where this object is in its own timeline on this peer: <see cref="CrackNet.PlaybackState.Pending"/> until playback
    /// reaches its first sample, <see cref="CrackNet.PlaybackState.Ending"/> once it despawned. The authority is always past
    /// pending. Read this instead of <c>Visible</c> to tell whether a projectile can hit yet.
    /// </summary>
    public PlaybackState PlaybackState
        => DespawnRequested || RemoteDespawned ? PlaybackState.Ending
            : IsAuthority || Shown ? PlaybackState.Playing
            : PlaybackState.Pending;

    private void Jump(Sample sample)
    {
        for (var i = 0; i < Properties.Count; i++)
            Properties[i].Node.SetValue(Properties[i].Property, sample.Values[i]);
    }

    internal bool Shown { get; private set; } = true;

    internal void SetShown(bool shown)
    {
        Shown = shown;
        if (Root is Node3D node) node.Visible = shown;
    }

    public override void _ExitTree()
    {
        if (Engine.IsEditorHint()) return;
        _body?.Exited();
        Unhang();
        foreach (var item in _attached.ToArray()) item.Unhang();
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

    internal static Type? TypeOfScript(string path)
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
            // Not inherited: a subclass of another script would carry both paths
            .FirstOrDefault(type => type.GetCustomAttribute<ScriptPathAttribute>(inherit: false)?.Path == path);

    private static bool HasOwnObject(Node node, NetworkObject? except)
    {
        foreach (var child in node.GetChildren())
            if (child is NetworkObject other && other != except) return true;
        return false;
    }

    internal sealed class Sample(Variant[] values, bool snap, bool despawned, Attachment? attachment = null)
    {
        public Variant[] Values { get; } = values;
        public bool Snap { get; } = snap;
        public bool Despawned { get; } = despawned;
        /// <summary>Where the object hangs at this tick, or null when it is free; the transform value is then relative to the anchor.</summary>
        public Attachment? Attachment { get; } = attachment;
    }

    /// <summary>Who simulates a <see cref="NetworkObject"/>, and taking or returning that by hand.</summary>
    public sealed class ObjectAuthority
    {
        private readonly NetworkObject _object;

        internal ObjectAuthority(NetworkObject obj) => _object = obj;

        /// <summary>The peer that simulates the object and sends its state: Godot's multiplayer authority of the root.</summary>
        public int Peer => _object.AuthorityPeer;

        /// <summary>True when this peer simulates the object and sends its state.</summary>
        public bool IsLocal => _object.IsAuthority;

        /// <summary>
        /// Takes authority over a free object this peer interacts with. Physics bodies do this themselves on contact;
        /// call it for a <see cref="ObjectKind.Custom"/> object or an interaction that is not contact. False when it is
        /// held by someone else, or when this peer is not connected; true means applied here and sent, not yet
        /// accepted by the host.
        /// </summary>
        public bool Take() => _object.TryTakeAuthority();

        /// <summary>
        /// Hands a free object this peer simulates back to the host. A settled <see cref="ObjectKind.Shared"/> physics
        /// body does this itself.
        /// </summary>
        public bool ReturnToHost() => _object.ReturnToHost();

        public override string ToString() => $"{Peer}";
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
