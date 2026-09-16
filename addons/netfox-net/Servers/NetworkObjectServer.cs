using Godot;
using Netfox.Core.Logging;
using Netfox.Core.Serialization;
using Netfox.Core.Time;
using Netfox.Internal;

namespace Netfox;

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
    public NetfoxContext Context { get; private set; } = NetfoxContext.Default;

    /// <summary>How many ticks behind the newest sample remote objects are shown.</summary>
    public double PlaybackDelayTicks { get; set; } = 3;

    /// <summary>State goes out every this many ticks: 2 at 30 Hz is 15 snapshots a second.</summary>
    public const int StateIntervalTicks = 2;

    /// <summary>An object whose state has not changed is sent again only this often.</summary>
    public const int RestHeartbeatTicks = 30;

    private static readonly NetfoxLogger Logger = NetfoxLogger.ForNetfox("NetworkObjectServer");

    private readonly List<NetworkObject> _objects = new();
    private readonly Dictionary<Node, NetworkObject> _byRoot = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, PlaybackClock> _clocks = new();

    /// <summary>
    /// What the host said about objects this guest does not have yet. A late joiner hears about every object as it
    /// connects, and MultiplayerSpawner delivers spawned ones on its own channel, in no particular order with ours.
    /// </summary>
    private readonly Dictionary<string, (int Authority, int Owner, int AuthoritySequence, int OwnershipSequence)> _pendingAuthority = new();
    private readonly int _maxPacketSize = NetfoxSettings.Instance.MaxSyncPacketSize;
    private NetworkCommandServer.Command _cmdState = null!;
    private NetworkCommandServer.Command _cmdAuthority = null!;
    private NetworkCommandServer.Command _cmdEvent = null!;

    public override void _EnterTree()
    {
        NetfoxRuntime.EnsureInitialized();
        Context = NetfoxContext.For(this);
        Context.NetworkObjectServer ??= this;
        if (Context.IsDefault) Instance ??= this;
    }

    public override void _Ready()
    {
        _cmdState = Context.NetworkCommandServer.RegisterCommandAt(CommandIds.ObjectState, HandleState, MultiplayerPeer.TransferModeEnum.Unreliable);
        _cmdAuthority = Context.NetworkCommandServer.RegisterCommandAt(CommandIds.ObjectAuthority, HandleAuthority, MultiplayerPeer.TransferModeEnum.Reliable);
        _cmdEvent = Context.NetworkCommandServer.RegisterCommandAt(CommandIds.ObjectEvent, HandleEvent, MultiplayerPeer.TransferModeEnum.Reliable);
        Context.NetworkTime.AfterTick += SendState;
        Context.NetworkEvents.OnPeerLeave += ErasePeer;
        Context.NetworkEvents.OnPeerJoin += SendAllAuthorityTo;
    }

    public override void _ExitTree()
    {
        if (Context.NetworkTime is { } time) time.AfterTick -= SendState;
        if (Context.NetworkEvents is { } events)
        {
            events.OnPeerLeave -= ErasePeer;
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
            obj.Apply(pending.Authority, pending.Owner, pending.AuthoritySequence, pending.OwnershipSequence);
    }

    internal void Deregister(NetworkObject obj)
    {
        _objects.Remove(obj);
        _byRoot.Remove(obj.Root!);
        Context.NetworkIdentityServer?.DeregisterNode(obj.Root!);
    }

    /// <summary>Forgets a peer's clock. Its objects keep their last displayed state.</summary>
    public void ErasePeer(int peer) => _clocks.Remove(peer);

    internal void ResetSession()
    {
        _clocks.Clear();
        _pendingAuthority.Clear();
        foreach (var obj in _objects) obj.Track.Clear();
    }

    /// <summary>The display tick for objects of <paramref name="peer"/>, or null before anything arrived from it.</summary>
    public double? GetDisplayTick(int peer) => _clocks.TryGetValue(peer, out var clock) ? clock.Tick : null;

    /// <summary>
    /// Sends an authority change this peer just applied: a guest asks the host, the host tells everyone.
    /// </summary>
    internal void SubmitAuthority(NetworkObject obj)
    {
        if (Multiplayer.IsServer()) SendAuthority(obj, 0);
        else SendAuthority(obj, NetworkObject.HostPeer);
    }

    private void SendAuthority(NetworkObject obj, int peer)
    {
        if (Context.NetworkIdentityServer.GetIdentifierOf(obj.Root!) is not { } identifier) return;
        var targets = peer == 0 ? Multiplayer.GetPeers() : [peer];
        foreach (var target in targets)
        {
            var writer = new ByteWriter();
            // By name, not id: this is reliable and rare, and a name resolves even before ids were exchanged
            NetRef.Encode(Core.Data.NetworkIdentityReference.OfFullName(identifier.FullName), writer);
            VarUint.Encode(obj.Authority, writer);
            VarUint.Encode(obj.Owner, writer);
            VarUint.Encode(obj.AuthoritySequence, writer);
            VarUint.Encode(obj.OwnershipSequence, writer);
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
        var authority = VarUint.DecodeInt(reader);
        var owner = VarUint.DecodeInt(reader);
        var authoritySequence = VarUint.DecodeInt(reader);
        var ownershipSequence = VarUint.DecodeInt(reader);

        var identifier = Context.NetworkIdentityServer.ResolveReference(sender, reference, allowQueue: false);
        NetworkObject? obj = null;
        if (identifier is not null) _byRoot.TryGetValue(identifier.Subject, out obj);

        if (!Multiplayer.IsServer())
        {
            if (sender != NetworkObject.HostPeer) return;
            if (obj is not null) obj.Apply(authority, owner, authoritySequence, ownershipSequence);
            else _pendingAuthority[reference.FullName] = (authority, owner, authoritySequence, ownershipSequence);
            return;
        }

        if (identifier is null || obj is null) return;

        var allowed = obj.Transferable
                      && obj.IsNewer(authoritySequence, ownershipSequence)
                      && (obj.Owner == 0 || obj.Owner == sender)
                      && (authority == sender || (authority == NetworkObject.HostPeer && obj.Authority == sender))
                      && (owner == 0 || owner == sender);

        if (allowed)
        {
            obj.Apply(authority, owner, authoritySequence, ownershipSequence);
            SendAuthority(obj, 0);
        }
        else
        {
            Logger.Debug("Rejected authority change on {0} from #{1}", identifier.FullName, sender);
            SendAuthority(obj, sender);
        }
    }

    /// <summary>On the host: tells a peer that just joined who has authority over and who holds every object.</summary>
    private void SendAllAuthorityTo(int peer)
    {
        if (!Multiplayer.IsServer()) return;
        foreach (var obj in _objects) SendAuthority(obj, peer);
    }

    // Two peers that briefly disagree about the authority would pass an event back and forth until they agree
    private const int MaxEventHops = 8;

    internal void SendEvent(NetworkObject obj, int target, int origin, Variant payload, int hops)
    {
        if (Context.NetworkIdentityServer.GetIdentifierOf(obj.Root!) is not { } identifier) return;
        var writer = new ByteWriter();
        NetRef.Encode(Core.Data.NetworkIdentityReference.OfFullName(identifier.FullName), writer);
        VarUint.Encode(origin, writer);
        VarUint.Encode(hops, writer);
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
        var payload = CompactValues.Decode(reader);

        if (Context.NetworkIdentityServer.ResolveReference(sender, reference, allowQueue: false) is not { } identifier
            || !_byRoot.TryGetValue(identifier.Subject, out var obj))
            return;

        if (obj.IsAuthority)
            obj.Receive(origin, payload);
        else if (hops < MaxEventHops)
            SendEvent(obj, obj.Authority, origin, payload, hops + 1);
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
            if (!obj.IsAuthority || identities.GetIdentifierOf(obj.Root!) is not { } identifier) continue;

            var writer = new ByteWriter();
            writer.PutU8(obj.TeleportPending ? (byte)1 : (byte)0);
            foreach (var (node, property, _) in obj.Properties)
                CompactValues.Encode(node.GetValue(property), writer);
            var body = writer.ToArray();

            // At rest: nothing new to say, apart from a heartbeat for peers that joined since or lost the last one
            var unchanged = obj.LastSentBody is { } last && last.AsSpan().SequenceEqual(body);
            if (unchanged && stateTick - obj.LastSentTick < RestHeartbeatTicks) continue;

            obj.LastSentBody = body;
            obj.LastSentTick = stateTick;
            obj.TeleportPending = false;
            sending.Add((obj, identifier, body));
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
            _clocks[sender] = clock = new PlaybackClock(PlaybackDelayTicks);
        clock.Observe(tick);

        while (reader.AvailableBytes > 0)
        {
            var reference = NetRef.Decode(reader);
            var length = VarUint.DecodeInt(reader);
            var end = reader.Position + length;

            var identifier = Context.NetworkIdentityServer.ResolveReference(sender, reference);
            if (identifier is null
                || !_byRoot.TryGetValue(identifier.Subject, out var obj)
                || obj.Root!.GetMultiplayerAuthority() != sender)
            {
                reader.Position = end;
                continue;
            }

            var teleport = reader.GetU8() != 0;
            var values = new Variant[obj.Properties.Count];
            for (var i = 0; i < values.Length; i++)
                values[i] = CompactValues.Decode(reader);
            reader.Position = end;

            // After a rest the sender skipped ticks on purpose: hold the resting value until just before this sample,
            // or playback would drift the whole way from where it came to rest
            if (obj.Track.TryGetNewest(out var newestTick, out var newest) && tick - newestTick > StateIntervalTicks * 3)
                obj.Track.Push(tick - StateIntervalTicks, new NetworkObject.Sample(newest.Values, false), clock.Tick);

            obj.Track.Push(tick, new NetworkObject.Sample(values, teleport), clock.Tick);
        }
    }

    public override void _Process(double delta)
    {
        var elapsedTicks = delta * Context.NetworkTime.Tickrate;
        foreach (var clock in _clocks.Values)
            clock.Advance(elapsedTicks);

        foreach (var obj in _objects)
        {
            if (obj.IsAuthority) continue;
            if (!_clocks.TryGetValue(obj.Root!.GetMultiplayerAuthority(), out var clock) || clock.Tick is not { } shown) continue;
            if (!obj.Track.TrySample(shown, out var from, out var to, out var fraction)) continue;
            Apply(obj, from, to, fraction);
        }
    }

    private static void Apply(NetworkObject obj, NetworkObject.Sample from, NetworkObject.Sample to, double fraction)
    {
        for (var i = 0; i < obj.Properties.Count; i++)
        {
            var (node, property, interpolate) = obj.Properties[i];
            var a = from.Values[i];
            var b = to.Values[i];
            var interpolator = Interpolators.FindInterpolatorFor(a);

            var value = interpolate && !to.Teleport && !ReferenceEquals(interpolator, Interpolators.DefaultInterpolator)
                ? interpolator.Apply(a, b, fraction)
                : fraction >= 1 ? b : a;
            node.SetValue(property, value);
        }

        if (!obj.Shown) obj.SetShown(true);
    }
}
