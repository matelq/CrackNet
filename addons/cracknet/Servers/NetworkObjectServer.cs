using CrackNet.Core.Data;
using CrackNet.Core.Logging;
using CrackNet.Core.Serialization;
using CrackNet.Core.Time;
using CrackNet.Internal;
using Godot;

namespace CrackNet;

/// <summary>The newest state tick received from a peer and the tick currently displayed for that peer.</summary>
/// <summary>
/// How old what a peer is shown is, averaged over the last second, in ticks: <see cref="NetworkTicks"/> is how old its
/// state was on arrival, <see cref="TotalTicks"/> how far its playback runs behind the local tick. The difference is
/// the time spent in the playback buffer.
/// </summary>
public readonly record struct PlaybackStatus(double NetworkTicks, double TotalTicks)
{
    public double PlaybackTicks => Math.Max(0, TotalTicks - NetworkTicks);
}

/// <summary>
/// Sends the state of every <see cref="NetworkObject"/> this peer is authority for, once per tick, and plays back the
/// state of every other one. Keeps one <see cref="PlaybackClock"/> per remote peer.
/// <para>
/// A packet is the tick, then one block per object: its identity reference, a length so a receiver that does not know
/// the object yet can skip it, a teleport flag and the values in property order. Packets are unreliable and split
/// between objects to stay under the packet size limit. A lost packet is not resent: the next tick supersedes it.
/// </para>
/// </summary>
public partial class NetworkObjectServer : Node
{
    public static NetworkObjectServer Instance { get; private set; } = null!;

    /// <summary>The stack this server belongs to; resolved when it enters the tree.</summary>
    public CrackNetContext Context { get; private set; } = CrackNetContext.Default;

    /// <summary>How many ticks behind the newest sample remote objects are shown.</summary>
    public double PlaybackDelayTicks { get; set; } = StateIntervalTicks + 0.5;

    /// <summary>The deepest a playback buffer grows to absorb jitter; also what a despawn waits out.</summary>
    public const double MaxPlaybackDepthTicks = 20;

    /// <summary>State goes out every this many ticks: 2 at 30 Hz is 15 snapshots a second.</summary>
    public const int StateIntervalTicks = 2;

    /// <summary>An object whose state has not changed is sent again only this often.</summary>
    public const int RestHeartbeatTicks = 30;

    private static readonly CrackNetLogger Logger = CrackNetLogger.ForCrackNet("NetworkObjectServer");

    private readonly List<NetworkObject> _objects = new();
    private readonly Dictionary<Node, NetworkObject> _byRoot = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, PlaybackClock> _clocks = new();

    private const int MaxPendingSamples = 256;
    private const ulong PendingSampleAgeMs = 5_000;
    private readonly List<PendingSample> _pendingSamples = [];
    private sealed record PendingSample(int Sender, NetworkIdentityReference Reference, int Tick, byte[] Body, ulong ReceivedAt);

    /// <summary>
    /// What the host said about objects this guest does not have yet. A late joiner hears about every object as it
    /// connects, and MultiplayerSpawner delivers spawned ones on its own channel, in no particular order with ours.
    /// </summary>
    private sealed record AuthorityRecord(
        int Authority,
        int Owner,
        int AuthoritySequence,
        int OwnershipSequence,
        bool Transferable,
        int TransferableSequence,
        string SpreadCause,
        int SpreadDepth,
        int SpreadLimit);

    private readonly Dictionary<string, AuthorityRecord> _pendingAuthority = new();
    private readonly int _maxPacketSize = CrackNetSettings.Instance.MaxSyncPacketSize;
    private NetworkCommandServer.Command _cmdState = null!;
    private NetworkCommandServer.Command _cmdAuthority = null!;
    private NetworkCommandServer.Command _cmdEvent = null!;
    internal Spawns Spawns { get; private set; } = null!;

    public override void _EnterTree()
    {
        CrackNetRuntime.EnsureInitialized();
        Context = CrackNetContext.For(this);
        Context.NetworkObjectServer ??= this;
        if (Context.IsDefault) Instance ??= this;
    }

    public override void _Ready()
    {
        _cmdState = Context.NetworkCommandServer.RegisterCommandAt(CommandIds.ObjectState, HandleState, MultiplayerPeer.TransferModeEnum.Unreliable);
        _cmdAuthority = Context.NetworkCommandServer.RegisterCommandAt(CommandIds.ObjectAuthority, HandleAuthority, MultiplayerPeer.TransferModeEnum.Reliable);
        _cmdEvent = Context.NetworkCommandServer.RegisterCommandAt(CommandIds.ObjectEvent, HandleEvent, MultiplayerPeer.TransferModeEnum.Reliable);
        Spawns = new Spawns(this);
        Context.NetworkTime.AfterTick += SendState;
        Context.NetworkEvents.OnPeerLeave += ErasePeer;
        // Spawns first: the authority records name objects the joiner has to have
        Context.NetworkEvents.OnPeerJoin += Spawns.SendAllTo;
        Context.NetworkEvents.OnPeerJoin += SendAllAuthorityTo;
    }

    public override void _ExitTree()
    {
        if (Context.NetworkTime is { } time) time.AfterTick -= SendState;
        if (Context.NetworkEvents is { } events)
        {
            events.OnPeerLeave -= ErasePeer;
            events.OnPeerJoin -= Spawns.SendAllTo;
            events.OnPeerJoin -= SendAllAuthorityTo;
        }
        if (ReferenceEquals(Context.NetworkObjectServer, this)) Context.NetworkObjectServer = null!;
        if (Instance == this) Instance = null!;
    }

    internal void Register(NetworkObject obj)
    {
        _objects.Add(obj);
        _byRoot[obj.Root!] = obj;
        Context.NetworkIdentityServer.RegisterNode(obj.Root!);

        if (Context.NetworkIdentityServer.GetIdentifierOf(obj.Root!) is { } identifier
            && _pendingAuthority.Remove(identifier.FullName, out var pending))
            ApplyRecord(obj, pending);

        ApplyPendingSamples(obj);
        obj.Registered = true;
    }

    /// <summary>Playback timing per peer: for readouts and checks rather than for game logic.</summary>
    public ServerDiagnostics Diagnostics => _diagnostics ??= new ServerDiagnostics(this);

    private ServerDiagnostics? _diagnostics;

    public sealed class ServerDiagnostics
    {
        private readonly NetworkObjectServer _server;

        internal ServerDiagnostics(NetworkObjectServer server) => _server = server;

        /// <summary>The display tick for objects of <paramref name="peer"/>, or null before anything arrived from it.</summary>
        public double? GetDisplayTick(int peer) => _server.GetDisplayTick(peer);

        /// <summary>How old what <paramref name="peer"/> is shown is, averaged over the last second, or null before any state arrived.</summary>
        public PlaybackStatus? GetPlaybackStatus(int peer) => _server.GetPlaybackStatus(peer);
    }

    internal NetworkObject? Find(Node root) => _byRoot.GetValueOrDefault(root);

    internal void Deregister(NetworkObject obj)
    {
        _objects.Remove(obj);
        _byRoot.Remove(obj.Root!);
        obj.Registered = false;
        Spawns?.Deregistered(obj);
        Context.NetworkIdentityServer?.DeregisterNode(obj.Root!);
    }

    /// <summary>
    /// Forgets a peer's clock. On the host, also takes back every object the peer simulated or held and tells everyone:
    /// otherwise a crate carried out of the session stays with nobody for good.
    /// </summary>
    public void ErasePeer(int peer)
    {
        _clocks.Remove(peer);
        _ages.Remove(peer);
        Spawns.ErasePeer(peer);
        if (!Multiplayer.IsServer()) return;

        foreach (var obj in _objects)
        {
            if (obj.Authority.Peer != peer && obj.ClaimedBy != peer) continue;
            // Players leave with their peer; the game frees them. What is left behind goes back to the host.
            if (!obj.Transferable) continue;
            obj.Apply(NetworkObject.HostPeer, 0, obj.AuthoritySequence + 1, obj.OwnershipSequence + 1,
                obj.Transferable, obj.TransferableSequence);
            SendAuthority(obj, 0);
        }
    }

    internal void ResetSession()
    {
        _clocks.Clear();
        _ages.Clear();
        _pendingAuthority.Clear();
        _pendingSamples.Clear();
        foreach (var obj in _objects)
        {
            obj.Track.Clear();
            obj.PlaybackCursor.Reset();
            obj.PlaybackStarted = false;
            obj.DisplayTick = null;
        }
    }

    /// <summary>The display tick for objects of <paramref name="peer"/>, or null before anything arrived from it.</summary>
    internal double? GetDisplayTick(int peer) => _clocks.TryGetValue(peer, out var clock) ? clock.Tick : null;

    /// <summary>
    /// How old what <paramref name="peer"/> is shown is, averaged over the last second, or null before any state arrived.
    /// <para>
    /// Measured on arrival and against the clock's running time rather than against the newest tick: a resting peer
    /// sends only a heartbeat a second, and "local tick minus newest tick" then read up to a second of delay that was
    /// never there.
    /// </para>
    /// </summary>
    internal PlaybackStatus? GetPlaybackStatus(int peer)
    {
        if (!_ages.TryGetValue(peer, out var ages) || ages.Network.Count == 0 || ages.Total.Count == 0) return null;
        return new PlaybackStatus(ages.Network.Average(sample => sample.Ticks), ages.Total.Average(sample => sample.Ticks));
    }

    private const ulong AgeWindowMs = 1000;
    private readonly Dictionary<int, (Queue<(ulong At, double Ticks)> Network, Queue<(ulong At, double Ticks)> Total)> _ages = new();

    private (Queue<(ulong At, double Ticks)> Network, Queue<(ulong At, double Ticks)> Total) AgesOf(int peer)
    {
        if (!_ages.TryGetValue(peer, out var ages)) _ages[peer] = ages = (new(), new());
        return ages;
    }

    private static void AddAge(Queue<(ulong At, double Ticks)> samples, double ticks)
    {
        var now = Godot.Time.GetTicksMsec();
        samples.Enqueue((now, ticks));
        while (samples.Count > 0 && now - samples.Peek().At > AgeWindowMs) samples.Dequeue();
    }

    private double LocalTick => Context.NetworkTime.Tick + Context.NetworkTime.TickFactor;

    /// <summary>
    /// Sends an authority change this peer just applied: a guest asks the host, the host tells everyone.
    /// </summary>
    internal void SubmitAuthority(NetworkObject obj)
    {
        if (Multiplayer.IsServer()) SendAuthority(obj, 0);
        else SendAuthority(obj, NetworkObject.HostPeer, obj.NextRequest());
    }

    // requestId is a guest's id for its request; answering is, on the host, the guest whose request this answers and
    // which alone gets that id back
    private void SendAuthority(NetworkObject obj, int peer, int requestId = 0, int answering = 0)
    {
        if (Context.NetworkIdentityServer.GetIdentifierOf(obj.Root!) is not { } identifier) return;
        var targets = peer == 0 ? Multiplayer.GetPeers() : [peer];
        foreach (var target in targets)
        {
            var writer = new ByteWriter();
            // By name, not id: this is reliable and rare, and a name resolves even before ids were exchanged
            NetRef.Encode(Core.Data.NetworkIdentityReference.OfFullName(identifier.FullName), writer);
            VarUint.Encode(target == answering || answering == 0 ? requestId : 0, writer);
            VarUint.Encode(obj.Authority.Peer, writer);
            VarUint.Encode(obj.ClaimedBy, writer);
            VarUint.Encode(obj.AuthoritySequence, writer);
            VarUint.Encode(obj.OwnershipSequence, writer);
            writer.PutU8(obj.Transferable ? (byte)1 : (byte)0);
            VarUint.Encode(obj.TransferableSequence, writer);
            writer.PutUtf8String(obj.SpreadCause);
            writer.PutI32(obj.SpreadDepth);
            writer.PutI32(obj.SpreadLimit);
            _cmdAuthority.Send(writer.ToArray(), target);
        }
    }

    /// <summary>
    /// On the host: accepts a guest's change when it is newer and the object is free or already the guest's, and tells
    /// everyone; otherwise tells the guest what stands. On a guest: whatever the host says stands.
    /// </summary>
    private void HandleAuthority(int sender, byte[] data)
    {
        var reader = new ByteReader(data);
        var reference = NetRef.Decode(reader);
        var requestId = VarUint.DecodeInt(reader);
        var authority = VarUint.DecodeInt(reader);
        var owner = VarUint.DecodeInt(reader);
        var authoritySequence = VarUint.DecodeInt(reader);
        var ownershipSequence = VarUint.DecodeInt(reader);
        var transferable = reader.GetU8() != 0;
        var transferableSequence = VarUint.DecodeInt(reader);
        var spreadCause = reader.GetUtf8String();
        var spreadDepth = reader.GetI32();
        var spreadLimit = reader.GetI32();
        var record = new AuthorityRecord(authority, owner, authoritySequence, ownershipSequence,
            transferable, transferableSequence, spreadCause, spreadDepth, spreadLimit);

        var identifier = Context.NetworkIdentityServer.ResolveReference(sender, reference, allowQueue: false);
        NetworkObject? obj = null;
        if (identifier is not null) _byRoot.TryGetValue(identifier.Subject, out obj);

        if (!Multiplayer.IsServer())
        {
            if (sender != NetworkObject.HostPeer) return;
            if (obj is not null)
            {
                Logger.Debug("AUTH host says {0}: authority {1} holder {2} seq {3}/{4} answering {5} (had authority {6} seq {7}/{8})",
                    obj.Root!.Name, authority, owner, ownershipSequence, authoritySequence, requestId,
                    obj.Authority.Peer, obj.OwnershipSequence, obj.AuthoritySequence);
                // An answer to an older request, or older news, while a later request is on its way: the host's answer
                // to that one is coming. Applied, it would undo that request here for a network round trip: a grabbed
                // crate let go in the hand and colliding with whatever it was carried through
                var outdated = obj.PendingRequest != 0 && requestId != obj.PendingRequest && !obj.IsNewer(authoritySequence, ownershipSequence);
                if (!outdated) ApplyRecord(obj, record);
                obj.Answered(requestId);
            }
            else _pendingAuthority[reference.FullName] = record;
            return;
        }

        if (identifier is null || obj is null) return;

        NetworkObject? cause = null;
        if (spreadCause.Length > 0
            && Context.NetworkIdentityServer.ResolveReference(sender,
                NetworkIdentityReference.OfFullName(spreadCause), allowQueue: false) is { } causeIdentifier)
            _byRoot.TryGetValue(causeIdentifier.Subject, out cause);

        var causeAllowed = spreadCause.Length == 0
                           || cause is not null
                           && cause.Authority.Peer == sender
                           && cause.SpreadsAuthority
                           && spreadDepth == cause.SpreadDepth + 1
                           && spreadLimit == cause.EffectiveSpreadLimit
                           && (spreadLimit < 0 || spreadDepth <= spreadLimit);

        var authorityAllowed = obj.Transferable
                               && obj.IsNewer(authoritySequence, ownershipSequence)
                               && (obj.ClaimedBy == 0 || obj.ClaimedBy == sender)
                               && (authority == sender || (authority == NetworkObject.HostPeer && obj.Authority.Peer == sender))
                               && (owner == 0 || owner == sender)
                               && causeAllowed;
        var configurationAllowed = transferableSequence > obj.TransferableSequence
                                   && sender == obj.Authority.Peer
                                   && authority == obj.Authority.Peer
                                   && owner == obj.ClaimedBy
                                   && authoritySequence == obj.AuthoritySequence
                                   && ownershipSequence == obj.OwnershipSequence;

        if (authorityAllowed || configurationAllowed)
        {
            if (authorityAllowed)
            {
                Logger.Debug("AUTH accepted {0} from #{1}: authority {2} holder {3} seq {4}/{5} cause '{6}'",
                    obj.Root!.Name, sender, authority, owner, ownershipSequence, authoritySequence, spreadCause);
                ApplyRecord(obj, record);
            }
            else
                obj.Apply(obj.Authority.Peer, obj.ClaimedBy, obj.AuthoritySequence, obj.OwnershipSequence,
                    transferable, transferableSequence, obj.SpreadCause, obj.SpreadDepth, obj.SpreadLimit);
            SendAuthority(obj, 0, requestId, answering: sender);
        }
        else
        {
            Logger.Debug("Rejected authority change on {0} from #{1}: transferable {2}, newer {3}, free {4}, cause {5} ({6}: authority {7}, depth {8}/{9})",
                identifier.FullName, sender, obj.Transferable, obj.IsNewer(authoritySequence, ownershipSequence),
                obj.ClaimedBy == 0 || obj.ClaimedBy == sender, causeAllowed, spreadCause,
                cause?.Authority.Peer, spreadDepth, cause is null ? -1 : cause.SpreadDepth + 1);
            SendAuthority(obj, sender, requestId, answering: sender);
        }
    }

    private static void ApplyRecord(NetworkObject obj, AuthorityRecord record)
        => obj.Apply(record.Authority, record.Owner, record.AuthoritySequence, record.OwnershipSequence,
            record.Transferable, record.TransferableSequence, record.SpreadCause, record.SpreadDepth, record.SpreadLimit);

    /// <summary>On the host: tells a peer that just joined who has authority over and who holds every object.</summary>
    private void SendAllAuthorityTo(int peer)
    {
        if (!Multiplayer.IsServer()) return;
        foreach (var obj in _objects) SendAuthority(obj, peer);
    }

    // Two peers that briefly disagree about the authority would pass an event back and forth until they agree
    private const int MaxEventHops = 8;

    internal void SendEvent(NetworkObject obj, int target, int origin, NetworkObject.EventKind kind, Variant payload, int hops)
    {
        if (Context.NetworkIdentityServer.GetIdentifierOf(obj.Root!) is not { } identifier) return;
        var writer = new ByteWriter();
        NetRef.Encode(Core.Data.NetworkIdentityReference.OfFullName(identifier.FullName), writer);
        VarUint.Encode(origin, writer);
        VarUint.Encode(hops, writer);
        writer.PutU8((byte)kind);
        CompactValues.Encode(payload, writer);
        _cmdEvent.Send(writer.ToArray(), target);
    }

    /// <summary>Raises an event on its object if this peer is the authority, and passes it on to the authority otherwise.</summary>
    private void HandleEvent(int sender, byte[] data)
    {
        var reader = new ByteReader(data);
        var reference = NetRef.Decode(reader);
        var origin = VarUint.DecodeInt(reader);
        var hops = VarUint.DecodeInt(reader);
        var kind = (NetworkObject.EventKind)reader.GetU8();
        var payload = CompactValues.Decode(reader);

        if (Context.NetworkIdentityServer.ResolveReference(sender, reference, allowQueue: false) is not { } identifier
            || !_byRoot.TryGetValue(identifier.Subject, out var obj))
            return;

        if (obj.Authority.IsLocal || hops < MaxEventHops)
            obj.Deliver(origin, kind, payload, obj.Authority.IsLocal ? hops : hops + 1);
        else
            Logger.Warning("Dropped an event for {0} after {1} hops: peers disagree about its authority", identifier.FullName, hops);
    }

    private void SendState(double delta, int tick)
    {
        var stateTick = tick + 1;
        if (stateTick % StateIntervalTicks != 0) return;
        if (Multiplayer.MultiplayerPeer is null or OfflineMultiplayerPeer) return;

        var identities = Context.NetworkIdentityServer;
        var sending = new List<(NetworkObject Object, NetworkIdentifier Identifier, byte[] Body)>();
        foreach (var obj in _objects)
        {
            if (!obj.Authority.IsLocal || identities.GetIdentifierOf(obj.Root!) is not { } identifier) continue;

            // Flags: 1 teleport, 2 resumed after a rest, 4 final despawn sample.
            var resumed = obj.LastSentBody is not null && stateTick - obj.LastSentTick > StateIntervalTicks;
            var writer = new ByteWriter();
            writer.PutU8((byte)((obj.SnapPending ? 1 : 0) | (resumed ? 2 : 0) | (obj.DespawnRequested ? 4 : 0)));
            foreach (var (node, property, _) in obj.Properties)
                CompactValues.Encode(node.GetValue(property), writer);
            var body = writer.ToArray();

            // At rest: nothing new to say, apart from a heartbeat for peers that joined since or lost the last one
            var unchanged = obj.LastSentBody is { } last && last.AsSpan(1).SequenceEqual(body.AsSpan(1));
            if (!obj.DespawnRequested && unchanged && stateTick - obj.LastSentTick < RestHeartbeatTicks) continue;

            if (body.Length > _maxPacketSize && !obj.WarnedOversized)
            {
                obj.WarnedOversized = true;
                Logger.Warning("{0} is {1} bytes, larger than a state packet ({2}): it goes out as a packet that may be "
                               + "fragmented and lost whole. Split the object or send large state as an event",
                    identifier.FullName, body.Length, _maxPacketSize);
            }

            obj.LastSentBody = body;
            obj.LastSentTick = stateTick;
            obj.SnapPending = false;
            sending.Add((obj, identifier, body));
            obj.Diagnostics.RaiseSampleSent(stateTick);
        }
        if (sending.Count == 0) return;

        var block = new ByteWriter();
        foreach (var peer in Multiplayer.GetPeers())
        {
            var packet = NewPacket(stateTick);
            var hasObjects = false;
            foreach (var (_, identifier, body) in sending)
            {
                block.Clear();
                NetRef.Encode(identifier.ReferenceFor(peer), block);
                VarUint.Encode(body.Length, block);
                block.PutData(body);

                if (hasObjects && packet.WrittenSpan.Length + block.WrittenSpan.Length > _maxPacketSize)
                {
                    _cmdState.Send(packet.ToArray(), peer);
                    packet = NewPacket(stateTick);
                }
                packet.PutData(block.WrittenSpan);
                hasObjects = true;
            }
            _cmdState.Send(packet.ToArray(), peer);
        }
    }

    private static ByteWriter NewPacket(int tick)
    {
        var packet = new ByteWriter();
        packet.PutI32(tick);
        return packet;
    }

    private void HandleState(int sender, byte[] data)
    {
        var reader = new ByteReader(data);
        var tick = reader.GetI32();

        if (!_clocks.TryGetValue(sender, out var clock))
            _clocks[sender] = clock = new PlaybackClock(PlaybackDelayTicks, maxDepthTicks: MaxPlaybackDepthTicks);
        clock.Observe(tick);
        // A state for tick N is taken as tick N-1 finishes, so on a clean link it arrives as N comes around
        AddAge(AgesOf(sender).Network, Math.Max(0, LocalTick - tick));

        while (reader.AvailableBytes > 0)
        {
            var reference = NetRef.Decode(reader);
            var length = VarUint.DecodeInt(reader);
            var body = reader.GetData(length).ToArray();

            var identifier = Context.NetworkIdentityServer.ResolveReference(sender, reference);
            if (identifier is null
                || !_byRoot.TryGetValue(identifier.Subject, out var obj))
            {
                BufferPendingSample(sender, reference, tick, body);
                continue;
            }
            if (obj.Root!.GetMultiplayerAuthority() != sender) continue;

            KeepSample(obj, clock, tick, body);
        }
    }

    private void BufferPendingSample(int sender, NetworkIdentityReference reference, int tick, byte[] body)
    {
        var now = Time.GetTicksMsec();
        PrunePendingSamples(now);
        _pendingSamples.Add(new PendingSample(sender, reference, tick, body, now));
        while (_pendingSamples.Count > MaxPendingSamples) _pendingSamples.RemoveAt(0);
    }

    private void PrunePendingSamples(ulong now)
        => _pendingSamples.RemoveAll(sample => now - sample.ReceivedAt > PendingSampleAgeMs);

    private void ApplyPendingSamples(NetworkObject? only = null)
    {
        PrunePendingSamples(Time.GetTicksMsec());
        for (var i = 0; i < _pendingSamples.Count;)
        {
            var pending = _pendingSamples[i];
            var identifier = Context.NetworkIdentityServer.ResolveReference(pending.Sender, pending.Reference, allowQueue: false);
            if (identifier is null || !_byRoot.TryGetValue(identifier.Subject, out var obj) || only is not null && obj != only)
            {
                i++;
                continue;
            }

            _pendingSamples.RemoveAt(i);
            if (obj.Root!.GetMultiplayerAuthority() != pending.Sender
                || !_clocks.TryGetValue(pending.Sender, out var clock))
                continue;
            KeepSample(obj, clock, pending.Tick, pending.Body);
        }
    }

    private static void KeepSample(NetworkObject obj, PlaybackClock clock, int tick, byte[] body)
    {
        var reader = new ByteReader(body);

        var flags = reader.GetU8();
        var teleport = (flags & 1) != 0;
        var resumed = (flags & 2) != 0;
        var despawned = (flags & 4) != 0;
        var values = new Variant[obj.Properties.Count];
        for (var i = 0; i < values.Length; i++)
            values[i] = CompactValues.Decode(reader);

        var shown = clock.Tick;
        if (obj.Track.Count == 0 && shown is { } shared)
        {
            shown = obj.PlaybackCursor.Start(tick, shared);
            obj.PlaybackStarted = true;
        }

        // After a rest the sender skipped ticks on purpose: hold the resting value until just before this sample,
        // or playback would drift the whole way from where it came to rest. After loss it did not, and holding
        // would freeze the object for the length of the outage and then jump.
        if (resumed && obj.Track.TryGetNewest(out var newestTick, out var newest) && tick - newestTick > StateIntervalTicks)
            obj.Track.Push(tick - StateIntervalTicks, new NetworkObject.Sample(newest.Values, false, false), shown);

        if (obj.Track.Push(tick, new NetworkObject.Sample(values, teleport, despawned), shown))
            obj.Diagnostics.RaiseSampleReceived(tick);
    }

    public override void _Process(double delta)
    {
        var elapsedTicks = delta * Context.NetworkTime.Tickrate;
        foreach (var (peer, clock) in _clocks)
        {
            clock.Advance(elapsedTicks);
            if (clock.Time is { } time) AddAge(AgesOf(peer).Total, Math.Max(0, LocalTick - time));
        }

        if (_pendingSamples.Count > 0) ApplyPendingSamples();

        foreach (var obj in _objects)
        {
            if (obj.Authority.IsLocal) continue;
            if (!_clocks.TryGetValue(obj.Root!.GetMultiplayerAuthority(), out var clock) || clock.Tick is not { } shown) continue;
            var objectTick = obj.PlaybackStarted ? obj.PlaybackCursor.Advance(shown, elapsedTicks) : shown;
            if (!obj.Track.TrySample(objectTick, out var from, out var to, out var fraction)) continue;
            obj.DisplayTick = objectTick;
            Apply(obj, from, to, fraction);
        }
    }

    private static void Apply(NetworkObject obj, NetworkObject.Sample from, NetworkObject.Sample to, double fraction)
    {
        // From the first despawn sample on. It is sent repeatedly, so playback spends the grace period between two
        // despawn samples, and waiting for the last one left the object hanging where it ended
        var reachedDespawn = from.Despawned || to.Despawned && fraction >= 1;
        for (var i = 0; i < obj.Properties.Count; i++)
        {
            var (node, property, interpolate) = obj.Properties[i];
            var a = from.Values[i];
            var b = to.Values[i];
            var interpolator = Interpolators.FindInterpolatorFor(a);

            var value = interpolate && !to.Snap && !ReferenceEquals(interpolator, Interpolators.DefaultInterpolator)
                ? interpolator.Apply(a, b, fraction)
                : fraction >= 1 ? b : a;
            node.SetValue(property, value);
        }

        if (reachedDespawn)
        {
            obj.RemoteDespawned = true;
            if (obj.Shown) obj.SetShown(false);
        }
        else if (!obj.Shown && !obj.RemoteDespawned)
        {
            obj.SetShown(true);
        }
    }
}
