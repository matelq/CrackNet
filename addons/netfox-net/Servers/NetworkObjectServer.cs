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

    private static readonly NetfoxLogger Logger = NetfoxLogger.ForNetfox("NetworkObjectServer");
    private static readonly NetworkSchemaSerializer Values = NetworkSchemas.Variant();

    private readonly List<NetworkObject> _objects = new();
    private readonly Dictionary<Node, NetworkObject> _byRoot = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, PlaybackClock> _clocks = new();
    private readonly int _maxPacketSize = NetfoxSettings.Instance.MaxSyncPacketSize;
    private NetworkCommandServer.Command _cmdState = null!;

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
        Context.NetworkTime.AfterTick += SendState;
        Context.NetworkEvents.OnPeerLeave += ErasePeer;
    }

    public override void _ExitTree()
    {
        if (Context.NetworkTime is { } time) time.AfterTick -= SendState;
        if (Context.NetworkEvents is { } events) events.OnPeerLeave -= ErasePeer;
        if (ReferenceEquals(Context.NetworkObjectServer, this)) Context.NetworkObjectServer = null!;
        if (Instance == this) Instance = null!;
    }

    internal void Register(NetworkObject obj)
    {
        _objects.Add(obj);
        _byRoot[obj.Root!] = obj;
        Context.NetworkIdentityServer.RegisterNode(obj.Root!);
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
        foreach (var obj in _objects) obj.Track.Clear();
    }

    /// <summary>The display tick for objects of <paramref name="peer"/>, or null before anything arrived from it.</summary>
    public double? GetDisplayTick(int peer) => _clocks.TryGetValue(peer, out var clock) ? clock.Tick : null;

    private void SendState(double delta, int tick)
    {
        if (Multiplayer.MultiplayerPeer is null or OfflineMultiplayerPeer) return;

        var identities = Context.NetworkIdentityServer;
        var block = new ByteWriter();
        foreach (var peer in Multiplayer.GetPeers())
        {
            var packet = NewPacket(tick + 1);
            var hasObjects = false;

            foreach (var obj in _objects)
            {
                if (!obj.IsAuthority || identities.GetIdentifierOf(obj.Root!) is not { } identifier) continue;

                block.Clear();
                NetRef.Encode(identifier.ReferenceFor(peer), block);
                var body = new ByteWriter();
                body.PutU8(obj.TeleportPending ? (byte)1 : (byte)0);
                foreach (var (node, property, _) in obj.Properties)
                    Values.Encode(node.GetValue(property), body);
                block.PutU16((ushort)body.WrittenSpan.Length);
                block.PutData(body.WrittenSpan);

                if (hasObjects && packet.WrittenSpan.Length + block.WrittenSpan.Length > _maxPacketSize)
                {
                    _cmdState.Send(packet.ToArray(), peer);
                    packet = NewPacket(tick + 1);
                }
                packet.PutData(block.WrittenSpan);
                hasObjects = true;
            }

            if (hasObjects) _cmdState.Send(packet.ToArray(), peer);
        }

        foreach (var obj in _objects)
            if (obj.IsAuthority) obj.TeleportPending = false;
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
            var length = reader.GetU16();
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
                values[i] = Values.Decode(reader);
            obj.Track.Push(tick, new NetworkObject.Sample(values, teleport), clock.Tick);
            reader.Position = end;
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
    }
}
