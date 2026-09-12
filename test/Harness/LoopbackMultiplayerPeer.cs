using Godot;

namespace Netfox.Tests;

/// <summary>
/// Routes packets between <see cref="LoopbackMultiplayerPeer"/> instances inside one process, so two netfox stacks can
/// talk to each other without a socket. Latency and packet loss are optional; loss only ever drops unreliable packets,
/// the way a real transport retransmits reliable ones.
/// </summary>
public sealed class LoopbackNetwork
{
    /// <summary>One-way delay applied to every packet, in milliseconds.</summary>
    public int LatencyMs { get; set; }

    /// <summary>Chance to drop an unreliable packet, 0 to 1.</summary>
    public double PacketLoss { get; set; }

    private readonly Random _rng = new(20260912);
    private readonly Dictionary<int, LoopbackMultiplayerPeer> _peers = new();

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

    public void Remove(LoopbackMultiplayerPeer peer) => _peers.Remove(peer.Id);

    internal void Send(int from, int to, byte[] data, MultiplayerPeer.TransferModeEnum mode, int channel)
    {
        foreach (var (id, peer) in _peers)
        {
            if (id == from) continue;
            if (to > 0 && id != to) continue;
            if (to < 0 && id == -to) continue;
            if (mode != MultiplayerPeer.TransferModeEnum.Reliable && PacketLoss > 0 && _rng.NextDouble() < PacketLoss) continue;

            peer.Receive(new LoopbackPacket(from, data, mode, channel), LatencyMs);
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
