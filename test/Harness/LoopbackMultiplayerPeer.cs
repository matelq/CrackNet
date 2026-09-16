using Godot;
using Netfox.Extras;

namespace Netfox.Tests;

/// <summary>
/// Routes packets between <see cref="LoopbackMultiplayerPeer"/> instances inside one process, so any number of netfox
/// stacks can talk to each other without a socket. Every peer is announced to every other, so this is a mesh: what
/// makes peer 1 the server is netfox asking the peer, not the routing. Latency and packet loss are optional; loss only ever drops unreliable packets,
/// the way a real transport retransmits reliable ones.
/// </summary>
public sealed class LoopbackNetwork
{
    /// <summary>One-way delay applied to every packet, in milliseconds. A link set with <see cref="SetLink"/> wins.</summary>
    public int LatencyMs { get; set; }

    /// <summary>Chance to drop an unreliable packet, 0 to 1. A link set with <see cref="SetLink"/> wins.</summary>
    public double PacketLoss { get; set; }

    /// <summary>Extra delay for reliable traffic, used to put spawn/control messages behind state in a check.</summary>
    public int ReliableExtraLatencyMs { get; set; }

    /// <summary>
    /// Outages on top of the steady loss, on every link at once. The same shape and the same function as the UDP
    /// proxy's, so a burst means the same thing in the harness as it does in a two-process run.
    /// </summary>
    public NetworkSimulator.Profile? Bursts { get; set; }

    private readonly Dictionary<(int, int), (int LatencyMs, double PacketLoss)> _links = new();

    /// <summary>
    /// Gives one link its own latency and loss, in both directions, instead of the network-wide values. Real peers do
    /// not share a connection: one player on a bad line is the normal case, and it must not be modelled by making
    /// everyone's line bad.
    /// </summary>
    public void SetLink(int a, int b, int latencyMs, double packetLoss = 0)
        => _links[LinkKey(a, b)] = (latencyMs, packetLoss);

    private (int LatencyMs, double PacketLoss) LinkBetween(int a, int b)
        => _links.TryGetValue(LinkKey(a, b), out var link) ? link : (LatencyMs, PacketLoss);

    private static (int, int) LinkKey(int a, int b) => a < b ? (a, b) : (b, a);

    private readonly Random _rng = new(20260912);
    private readonly Dictionary<int, LoopbackMultiplayerPeer> _peers = new();

    /// <summary>Bytes and packets that were handed to the network, per sending peer. Counted before loss is applied.</summary>
    public Dictionary<int, (long Bytes, long Packets)> Traffic { get; } = new();

    public void ResetTraffic() => Traffic.Clear();

    public (long Bytes, long Packets) TrafficFrom(int peer) => Traffic.GetValueOrDefault(peer);

    public LoopbackMultiplayerPeer CreatePeer(int id)
    {
        var peer = new LoopbackMultiplayerPeer(this, id);
        _peers[id] = peer;
        return peer;
    }

    /// <summary>
    /// Announces every peer to every other. This is what makes each MultiplayerAPI consider itself connected, so call it
    /// once both stacks have their peer assigned.
    /// </summary>
    public void Connect()
    {
        foreach (var peer in _peers.Values)
            peer.MarkConnected();

        foreach (var peer in _peers.Values)
            foreach (var other in _peers.Values)
                if (!ReferenceEquals(peer, other))
                    peer.AnnouncePeer(other.Id);
    }

    /// <summary>Connects a peer that joins after the others, announcing it in both directions.</summary>
    public void ConnectLate(LoopbackMultiplayerPeer peer)
    {
        peer.MarkConnected();
        foreach (var other in _peers.Values)
        {
            if (ReferenceEquals(peer, other)) continue;
            peer.AnnouncePeer(other.Id);
            other.AnnouncePeer(peer.Id);
        }
    }

    /// <summary>Takes a peer out of the network and tells the others it left, the way a transport does.</summary>
    public void Remove(LoopbackMultiplayerPeer peer)
    {
        if (!_peers.Remove(peer.Id)) return;
        foreach (var other in _peers.Values)
            other.AnnouncePeerLeft(peer.Id);
    }

    internal void Send(int from, int to, byte[] data, MultiplayerPeer.TransferModeEnum mode, int channel)
    {
        var counted = Traffic.GetValueOrDefault(from);
        Traffic[from] = (counted.Bytes + data.Length, counted.Packets + 1);

        foreach (var (id, peer) in _peers)
        {
            if (id == from) continue;
            if (to > 0 && id != to) continue;
            if (to < 0 && id == -to) continue;

            var link = LinkBetween(from, id);
            if (mode == MultiplayerPeer.TransferModeEnum.Reliable)
            {
                peer.Receive(new LoopbackPacket(from, data, mode, channel), link.LatencyMs + ReliableExtraLatencyMs);
                continue;
            }
            if (link.PacketLoss > 0 && _rng.NextDouble() < link.PacketLoss) continue;
            if (Bursts is { } bursts && NetworkSimulator.InLossBurst(bursts, Time.GetTicksMsec())) continue;

            peer.Receive(new LoopbackPacket(from, data, mode, channel), link.LatencyMs);
        }
    }
}

internal readonly record struct LoopbackPacket(int From, byte[] Data, MultiplayerPeer.TransferModeEnum Mode, int Channel);

/// <summary>A MultiplayerPeer whose transport is a <see cref="LoopbackNetwork"/> in the same process.</summary>
public partial class LoopbackMultiplayerPeer : MultiplayerPeerExtension
{
    public int Id { get; }

    private readonly LoopbackNetwork _network;
    private readonly List<(ulong At, LoopbackPacket Packet)> _pending = new();
    private readonly Queue<LoopbackPacket> _ready = new();

    private ConnectionStatus _status;
    private int _target = (int)TargetPeerBroadcast;
    private TransferModeEnum _mode = TransferModeEnum.Reliable;
    private int _channel;

    internal LoopbackMultiplayerPeer(LoopbackNetwork network, int id)
    {
        _network = network;
        Id = id;
        // Clients report Connecting first: MultiplayerAPI emits connected_to_server on the transition to Connected.
        _status = id == 1 ? ConnectionStatus.Connected : ConnectionStatus.Connecting;
    }

    internal void MarkConnected() => _status = ConnectionStatus.Connected;

    internal void AnnouncePeer(int id) => EmitSignal(MultiplayerPeer.SignalName.PeerConnected, id);

    internal void AnnouncePeerLeft(int id) => EmitSignal(MultiplayerPeer.SignalName.PeerDisconnected, id);

    internal void Receive(LoopbackPacket packet, int latencyMs)
    {
        if (latencyMs <= 0)
        {
            _ready.Enqueue(packet);
            return;
        }

        _pending.Add((Time.GetTicksMsec() + (ulong)latencyMs, packet));
    }

    public override int _GetUniqueId() => Id;
    public override bool _IsServer() => Id == 1;
    public override bool _IsServerRelaySupported() => false;
    public override ConnectionStatus _GetConnectionStatus() => _status;
    public override int _GetMaxPacketSize() => 1 << 20;

    public override void _SetTargetPeer(int peer) => _target = peer;
    public override void _SetTransferMode(TransferModeEnum mode) => _mode = mode;
    public override TransferModeEnum _GetTransferMode() => _mode;
    public override void _SetTransferChannel(int channel) => _channel = channel;
    public override int _GetTransferChannel() => _channel;

    public override int _GetAvailablePacketCount() => _ready.Count;
    public override int _GetPacketPeer() => _ready.Count > 0 ? _ready.Peek().From : 0;
    public override TransferModeEnum _GetPacketMode() => _ready.Count > 0 ? _ready.Peek().Mode : TransferModeEnum.Reliable;
    public override int _GetPacketChannel() => _ready.Count > 0 ? _ready.Peek().Channel : 0;
    public override byte[] _GetPacketScript() => _ready.Count > 0 ? _ready.Dequeue().Data : [];

    public override Error _PutPacketScript(byte[] buffer)
    {
        if (_status != ConnectionStatus.Connected) return Error.Unconfigured;

        _network.Send(Id, _target, (byte[])buffer.Clone(), _mode, _channel);
        return Error.Ok;
    }

    public override void _Poll()
    {
        if (_pending.Count == 0) return;

        var now = Time.GetTicksMsec();
        for (var i = 0; i < _pending.Count;)
        {
            if (_pending[i].At > now) { i++; continue; }
            _ready.Enqueue(_pending[i].Packet);
            _pending.RemoveAt(i);
        }
    }

    public override void _Close()
    {
        _status = ConnectionStatus.Disconnected;
        _pending.Clear();
        _ready.Clear();
        _network.Remove(this);
    }

    public override void _DisconnectPeer(int peer, bool force) { }
    public override bool _IsRefusingNewConnections() => false;
    public override void _SetRefuseNewConnections(bool enable) { }
}
