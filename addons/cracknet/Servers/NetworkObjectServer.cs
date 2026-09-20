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
/// the time spent in the playback buffer. The <c>Ms</c> properties are the same in milliseconds, for a HUD.
/// </summary>
public readonly record struct PlaybackStatus(double NetworkTicks, double TotalTicks, double MillisecondsPerTick)
{
    public double PlaybackTicks => Math.Max(0, TotalTicks - NetworkTicks);
    public double NetworkMs => NetworkTicks * MillisecondsPerTick;
    public double TotalMs => TotalTicks * MillisecondsPerTick;
    public double PlaybackMs => PlaybackTicks * MillisecondsPerTick;
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

    /// <summary>
    /// The least number of ticks behind the newest sample remote objects are shown, before jitter adds to it: two send
    /// intervals and a margin, so one lost packet does not empty the buffer: with one interval and a half-tick margin,
    /// 30 Hz snapshots left the loss test drawing twice the jumps.
    /// </summary>
    public double PlaybackDelayTicks
    {
        get => _playbackDelayTicks ?? StateIntervalTicks * 2 + 0.5;
        set => _playbackDelayTicks = value;
    }

    private double? _playbackDelayTicks;

    /// <summary>
    /// Physics steps between two snapshots, from <c>cracknet/time/state_interval_ticks</c>: 2 at Godot's default
    /// 60 Hz physics is 30 snapshots a second.
    /// </summary>
    public int StateIntervalTicks { get; set; } = Math.Max(1, CrackNetSettings.Instance.StateIntervalTicks);

    // Times rather than ticks: the tickrate is the physics rate, which a project sets for itself
    private const double MaxPlaybackDepthSeconds = 2.0 / 3;
    private const double RestHeartbeatSeconds = 1;
    private const double ResyncSeconds = 1;
    private const double MaxLeadSeconds = 4.0 / 3;   // past the heartbeat gap, so motion after a rest shows at once
    private const double LatenessWindowSeconds = 20;

    private double Tickrate => Context.NetworkTime.Tickrate;

    /// <summary>The deepest a playback buffer grows to absorb jitter, in ticks; also what a despawn waits out.</summary>
    public double MaxPlaybackDepthTicks => MaxPlaybackDepthSeconds * Tickrate;

    /// <summary>An object whose state has not changed is sent again only this often, in ticks.</summary>
    private int RestHeartbeatTicks => (int)Math.Round(RestHeartbeatSeconds * Tickrate);

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
        int SpreadLimit,
        NetworkObject.Attachment? ClaimAttachment);

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
        AddChild(new AttachmentPlacer { Server = this });
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
            // What is left behind goes back to the host; a player carried by the leaving peer is put down; players
            // leave with their peer, the game frees them
            if (obj.Transferable)
                obj.Apply(NetworkObject.HostPeer, 0, obj.AuthoritySequence + 1, obj.OwnershipSequence + 1, obj.Transferable, obj.TransferableSequence);
            else if (obj.ClaimedBy == peer)
                obj.Apply(obj.Authority.Peer, 0, obj.AuthoritySequence, obj.OwnershipSequence + 1, obj.Transferable, obj.TransferableSequence);
            else continue;
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
    internal double? GetDisplayTick(int peer) => _clocks.TryGetValue(peer, out var clock) ? ShownOf(clock) : null;

    /// <summary>
    /// Where this peer's objects are shown: its own clock, at its own link's depth. A screen therefore holds as many
    /// moments as it has peers, and two objects from different peers that touch - a player and the platform under it
    /// - are places at two moments, which is the limitation written up in #74 and on the parked/common-display-time
    /// branch. The one time per screen that removed it charged every player on the screen the depth of the worst live
    /// link: 117 ms became 695 ms with one 300 ms guest present, and the screen held still 415 ms as it joined.
    /// </summary>
    private static double? ShownOf(PlaybackClock clock) => clock.Tick;

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
        return new PlaybackStatus(ages.Network.Average(sample => sample.Ticks), ages.Total.Average(sample => sample.Ticks),
            1000.0 / Context.NetworkTime.Tickrate);
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
            writer.PutUtf8String(obj.ClaimAttachment?.Carrier ?? "");
            writer.PutUtf8String(obj.ClaimAttachment?.Anchor ?? "");
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
        var claimCarrier = reader.GetUtf8String();
        var claimAnchor = reader.GetUtf8String();
        var record = new AuthorityRecord(authority, owner, authoritySequence, ownershipSequence,
            transferable, transferableSequence, spreadCause, spreadDepth, spreadLimit,
            claimCarrier.Length > 0 ? new NetworkObject.Attachment(claimCarrier, claimAnchor) : null);

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
        // Carrying a player: its authority stays, only the holder changes. Picked up by the requester while free;
        // put down by its carrier or by itself
        var carryAllowed = !obj.Transferable
                           && authority == obj.Authority.Peer
                           && authoritySequence == obj.AuthoritySequence
                           && obj.IsNewer(authoritySequence, ownershipSequence)
                           && (owner == sender && obj.ClaimedBy == 0
                               || owner == 0 && (obj.ClaimedBy == sender || obj.Authority.Peer == sender));
        var configurationAllowed = transferableSequence > obj.TransferableSequence
                                   && sender == obj.Authority.Peer
                                   && authority == obj.Authority.Peer
                                   && owner == obj.ClaimedBy
                                   && authoritySequence == obj.AuthoritySequence
                                   && ownershipSequence == obj.OwnershipSequence;

        if (authorityAllowed || carryAllowed || configurationAllowed)
        {
            if (authorityAllowed || carryAllowed)
            {
                Logger.Debug("AUTH accepted {0} from #{1}: authority {2} holder {3} seq {4}/{5} cause '{6}'",
                    obj.Root!.Name, sender, authority, owner, ownershipSequence, authoritySequence, spreadCause);
                ApplyRecord(obj, record);
            }
            else
                obj.Apply(obj.Authority.Peer, obj.ClaimedBy, obj.AuthoritySequence, obj.OwnershipSequence,
                    transferable, transferableSequence, obj.SpreadCause, obj.SpreadDepth, obj.SpreadLimit, claimAttachment: obj.ClaimAttachment);
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
            record.Transferable, record.TransferableSequence, record.SpreadCause, record.SpreadDepth, record.SpreadLimit,
            claimAttachment: record.ClaimAttachment);

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

            // Flags: 1 teleport, 2 resumed after a rest, 4 final despawn sample, 8 first since this peer took it,
            // 16 attached: the carrier and anchor follow, and the transform is relative to the anchor; 32 with it:
            // standing on the carrier rather than held by it.
            var resumed = obj.LastSentBody is not null && stateTick - obj.LastSentTick > StateIntervalTicks;
            var attachment = obj.SentAttachment();
            var writer = new ByteWriter();
            writer.PutU8((byte)((obj.SnapPending ? 1 : 0) | (resumed ? 2 : 0) | (obj.DespawnRequested ? 4 : 0)
                                | (obj.LastSentBody is null ? FirstSinceTaken : 0) | (attachment is null ? 0 : Attached)
                                | (attachment is { Riding: true } ? Riding : 0)));
            if (attachment is not null)
            {
                writer.PutUtf8String(attachment.Carrier);
                writer.PutUtf8String(attachment.Anchor);
            }
            for (var i = 0; i < obj.Properties.Count; i++)
                CompactValues.Encode(obj.ValueToSend(i), writer);
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
            _clocks[sender] = clock = new PlaybackClock(PlaybackDelayTicks, resyncTicks: ResyncSeconds * Tickrate,
                maxLeadTicks: MaxLeadSeconds * Tickrate, maxDepthTicks: MaxPlaybackDepthTicks,
                latenessWindowTicks: LatenessWindowSeconds * Tickrate);
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
            if (obj.Root!.GetMultiplayerAuthority() != sender)
            {
                // A new authority's state comes straight here, its authority change the long way through the host:
                // hold it until that change arrives, or the object sits still and then jumps into the middle
                var now = Time.GetTicksMsec();
                obj.EarlySamples.RemoveAll(sample => now - sample.ReceivedAt > EarlySampleAgeMs);
                obj.EarlySamples.Add((sender, tick, body, now));
                continue;
            }

            KeepSample(obj, clock, tick, body);
        }
    }

    private const ulong EarlySampleAgeMs = 1_000;
    private const byte Resumed = 2;
    private const byte FirstSinceTaken = 8;
    private const byte Attached = 16;
    private const byte Riding = 32;

    /// <summary>
    /// <paramref name="obj"/> just changed authority here: the new authority's samples that came first are played
    /// from the start of its flight; anyone else's are dropped, since the host did not give it to them.
    /// </summary>
    internal void ReplayEarlySamples(NetworkObject obj)
    {
        if (obj.EarlySamples.Count == 0) return;
        var now = Time.GetTicksMsec();
        var authority = obj.Root!.GetMultiplayerAuthority();
        var early = obj.EarlySamples.Where(sample => sample.Sender == authority && now - sample.ReceivedAt <= EarlySampleAgeMs)
            .OrderBy(sample => sample.Tick).ToArray();
        obj.EarlySamples.Clear();
        if (!_clocks.TryGetValue(authority, out var clock)) return;
        // From the sample that opened this authority's turn: the ones before it are the end of an earlier turn of the
        // same peer, still in flight when it lost the object. Without that sample (lost), none are safe to play
        var opening = Array.FindLastIndex(early, sample => (sample.Body[0] & FirstSinceTaken) != 0);
        if (opening < 0) return;
        foreach (var sample in early[opening..]) KeepSample(obj, clock, sample.Tick, sample.Body);
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

    /// <summary>How long a sample waits for the one before it at most, in state intervals: packets swap places by a few milliseconds.</summary>
    private const int ReorderWaitIntervals = 1;

    private void KeepSample(NetworkObject obj, PlaybackClock clock, int tick, byte[] body)
    {
        // A sample that is neither the first after a rest nor the first since a take says there was one an interval
        // before it. Not here yet, that one is late or lost: it is waited for a little, since played now the pair
        // around the hole would be interpolated as the sender's motion, and a resumed sample arriving behind it could
        // no longer hold the rest it ends (the playground's crate drifted out of the hand for a second before a throw)
        if ((body[0] & (Resumed | FirstSinceTaken)) == 0 && obj.Track.TryGetNewest(out var newestTick, out _) && tick - StateIntervalTicks > newestTick)
        {
            obj.HeldSamples.Add((tick, body, Time.GetTicksMsec()));
            return;
        }
        Keep(obj, clock, tick, body);
        ReleaseHeld(obj, clock, all: false);
    }

    /// <summary>
    /// How far apart two samples put the body in this peer's world; null when an anchor is not here, or the root is
    /// not a physics body, and then the state is not carried: a body's motion is the line to draw, other state
    /// (a synced position of the game's own, say) is the new authority's to say from its first sample on.
    /// </summary>
    private static float? ApartBy(NetworkObject obj, NetworkObject.Sample a, Variant[] bValues, NetworkObject.Attachment? bAttachment)
    {
        if (obj.Root is not PhysicsBody3D) return null;
        var here = obj.WorldOf(a.Attachment, a.Values[0].AsTransform3D());
        var there = obj.WorldOf(bAttachment, bValues[0].AsTransform3D());
        return here is { } from && there is { } to ? from.Origin.DistanceTo(to.Origin) : null;
    }

    /// <summary>Plays the held samples whose predecessor has landed, in order; all of them when the wait is over.</summary>
    private void ReleaseHeld(NetworkObject obj, PlaybackClock clock, bool all)
    {
        var held = obj.HeldSamples;
        held.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        while (held.Count > 0 && (all || obj.Track.TryGetNewest(out var newest, out _) && held[0].Tick - StateIntervalTicks <= newest))
        {
            var (tick, body, _) = held[0];
            held.RemoveAt(0);
            Keep(obj, clock, tick, body);
        }
    }

    private void Keep(NetworkObject obj, PlaybackClock clock, int tick, byte[] body)
    {
        var reader = new ByteReader(body);

        var flags = reader.GetU8();
        var teleport = (flags & 1) != 0;
        var resumed = (flags & Resumed) != 0;
        var despawned = (flags & 4) != 0;
        var attachment = (flags & Attached) != 0
            ? new NetworkObject.Attachment(reader.GetUtf8String(), reader.GetUtf8String(), (flags & Riding) != 0)
            : null;
        var values = new Variant[obj.Properties.Count];
        for (var i = 0; i < values.Length; i++)
            values[i] = CompactValues.Decode(reader);

        var shown = ShownOf(clock);
        if (shown is { } shared && obj.CarriedTick is { } carried)
        {
            // The new authority's first sample: played from the state carried over the change of hands when it is
            // later than that state, as it normally is, and within the distance a handover is smoothed over; older,
            // it leads nowhere from there, and further away it is a relocation to show as one, not a flight across
            if (tick > carried && obj.Track.TryGetNewest(out _, out var kept) && ApartBy(obj, kept, values, attachment) is { } apart && apart <= obj.MaxSmoothingDistance)
                shown = obj.PlaybackCursor.Start(carried, shared);
            else
            {
                // The carried tick leads nowhere from here: later than this sample, so the two are out of order on
                // one track, or far enough apart to be a relocation. Playback starts clean at this sample and the
                // body slides to it, where it used to be put there in one frame - two peers ten times apart in link
                // depth hand a crate back and forth and every screen showed the step as a teleport (#80)
                obj.CrossingTo();
                obj.Track.Clear();
                shown = obj.PlaybackCursor.Start(tick, shared);
            }
            obj.CarriedTick = null;
            obj.PlaybackStarted = true;
        }
        else if (obj.Track.Count == 0 && shown is { } first)
        {
            shown = obj.PlaybackCursor.Start(tick, first);
            obj.PlaybackStarted = true;
        }

        // After a rest the sender skipped ticks on purpose: hold the resting value until just before this sample,
        // or playback would drift the whole way from where it came to rest. After loss it did not, and holding
        // would freeze the object for the length of the outage and then jump. A change of hung or free across the
        // gap holds either way: it is instantaneous, and the flag that says which it was may be on the lost packet.
        // Against the sample before this one in the track, not the newest: the one after it may have come first
        if (obj.Track.TryGetBefore(tick, out var previousTick, out var previous) && tick - previousTick > StateIntervalTicks
            && (resumed || previous.Attachment != attachment))
            obj.Track.Push(tick - StateIntervalTicks, new NetworkObject.Sample(previous.Values, false, false, previous.Attachment), shown);

        if (obj.Track.Push(tick, new NetworkObject.Sample(values, teleport, despawned, attachment), shown))
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

        var now = Time.GetTicksMsec();
        var waitMs = (ulong)(1000.0 * ReorderWaitIntervals * StateIntervalTicks / Context.NetworkTime.Tickrate);
        foreach (var obj in _objects)
        {
            if (obj.Authority.IsLocal) continue;
            if (!_clocks.TryGetValue(obj.Root!.GetMultiplayerAuthority(), out var clock) || ShownOf(clock) is not { } shown) continue;
            var objectTick = obj.PlaybackStarted ? obj.PlaybackCursor.Advance(shown, elapsedTicks) : shown;
            // Never back: an object's display tick only ever moves forward, whatever its clock does
            if (obj.DisplayTick is { } before && objectTick < before) objectTick = before;
            // Held samples are played once the wait is over, or sooner if playback has reached the last sample it has:
            // after a loss, a stall there would be worse than the interpolation across the hole the wait is against
            if (obj.HeldSamples.Count > 0 && (now - obj.HeldSamples.Min(sample => sample.HeldAt) >= waitMs
                                              || obj.Track.TryGetNewest(out var newestTick, out _) && objectTick >= newestTick))
                ReleaseHeld(obj, clock, all: true);
            if (!obj.Track.TrySample(objectTick, out var from, out var to, out var fraction)) continue;
            obj.DisplayTick = objectTick;
            Apply(obj, from, to, fraction);
        }
    }

    /// <summary>
    /// Puts every attached item on its anchor, carriers before what hangs on them. Once per frame after everything
    /// else has processed, from <see cref="AttachmentPlacer"/>.
    /// </summary>
    internal void PlaceAttached()
    {
        foreach (var obj in _objects)
            if (obj.Carrier is null) PlaceHangingOn(obj);
    }

    private static void PlaceHangingOn(NetworkObject carrier)
    {
        foreach (var item in carrier.Hanging.ToArray())
        {
            item.Place();
            PlaceHangingOn(item);
        }
    }

    private static void Apply(NetworkObject obj, NetworkObject.Sample from, NetworkObject.Sample to, double fraction)
    {
        // From the first despawn sample on. It is sent repeatedly, so playback spends the grace period between two
        // despawn samples, and waiting for the last one left the object hanging where it ended
        var reachedDespawn = from.Despawned || to.Despawned && fraction >= 1;
        // Hung or free as of the sample playback is coming from, and as of the one it reached once the fraction is 1:
        // the clock holds exactly on the newest sample of a resting object, so that pair is a steady state, not a
        // moment. The switch happens when playback reaches the sample that made it, at the carrier's display tick, not
        // when the host's record of the claim arrived. Across the switch a world position and an anchor offset have no
        // line between them as sent, but both are places in this peer's world: the object is drawn along the line
        // between those, in the state it came from, so a gesture counter bumped in the tick of the change still
        // changes in the frame the state does. Held instead, it stood for a state interval and then jumped it
        var atEnd = fraction >= 1;
        var sampled = atEnd ? to.Attachment : from.Attachment;
        // What this peer let go of stays let go: the player's samples from before its peer heard of the release still
        // say hung, and they do not put it back in the hand here. Believed again from the first free sample past the
        // record of the release, since that peer heard a moment earlier or later than this one
        // Let go of here, and its stream has not caught up: the samples still stand where it was before the grab,
        // lead into the hand, or hang from it. Shown free where it is until a free sample past the record of the
        // release is reached
        var letGo = false;
        if (obj.Released is { } released && obj.DisplayTick is { } shownTick)
        {
            if (from.Attachment is null && to.Attachment is null && shownTick >= released.Tick) obj.Released = null;
            else
            {
                sampled = null;
                letGo = true;
            }
        }
        var attachment = sampled ?? obj.ClaimedHere;
        var switching = from.Attachment != to.Attachment;
        // A world position and an anchor offset are places at two moments: the platform shown at the host's depth,
        // the player at its own. The line between them is a slide of that gap over two ticks; the Visual smoothing
        // spreads it over its own time instead
        if (switching) obj.Switching();
        for (var i = 0; i < obj.Properties.Count; i++)
        {
            var (node, property, interpolate) = obj.Properties[i];
            var a = from.Values[i];
            var b = to.Values[i];
            var interpolator = Interpolators.FindInterpolatorFor(a);
            var isTransform = i == 0 && obj.Root is Node3D;

            // A hung transform is an offset from the hand, not a place, and the way into the hand was shown already:
            // the copy stays where it is until the free sample is in sight, then goes from the hand to it
            if (isTransform && letGo)
            {
                if (switching && from.Attachment is not null && to.Attachment is null && !atEnd
                    && obj.WorldOf(from.Attachment, a.AsTransform3D()) is { } hand && obj.WorldOf(null, b.AsTransform3D()) is { } free)
                    node.SetValue(property, hand.InterpolateWith(free, (float)fraction));
                continue;
            }
            if (isTransform && switching && !atEnd && interpolate && !to.Snap && attachment == from.Attachment
                && obj.WorldOf(from.Attachment, a.AsTransform3D()) is { } fromWorld && obj.WorldOf(to.Attachment, b.AsTransform3D()) is { } toWorld)
            {
                var world = fromWorld.InterpolateWith(toWorld, (float)fraction);
                if (attachment is null) node.SetValue(property, world);
                else obj.ShowAttached(attachment, obj.OffsetOf(attachment, world) ?? a.AsTransform3D());
                continue;
            }
            var value = interpolate && !to.Snap && !(switching && isTransform) && !ReferenceEquals(interpolator, Interpolators.DefaultInterpolator)
                ? interpolator.Apply(a, b, fraction)
                : atEnd ? b : a;
            // A player this peer asked to carry is put in the hand before its own stream says so: its samples still
            // carry a world position, which is not an offset
            if (isTransform && attachment is not null) obj.ShowAttached(attachment, sampled is null ? Transform3D.Identity : value.AsTransform3D());
            else node.SetValue(property, value);
        }
        if (attachment is null) obj.ShowFree();
        // The frame the snap lands in: reaching the snap sample, or already past it
        if (to.Snap && fraction >= 1 || from.Snap) obj.SnapApplied();

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
