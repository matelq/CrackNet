using Godot;

namespace Netfox.Extras;

/// <summary>
/// A transport wrapper that applies a <see cref="NetworkSimulator.Profile"/> once, on the sending side of each
/// remote link. Reliable packets are delayed but never lost; unreliable packets also see the profile's steady and
/// burst loss. The wrapped transport still owns connection establishment and packet delivery.
/// </summary>
public partial class SimulatedMultiplayerPeer : MultiplayerPeerExtension
{
    private readonly MultiplayerPeer _inner;
    private readonly Func<int, NetworkSimulator.Profile> _profileFor;
    private readonly Random _random;
    private readonly HashSet<int> _peers = [];
    private readonly List<PendingPacket> _pending = [];
    private readonly Dictionary<int, ulong> _lastReliableSend = [];

    private int _target = (int)TargetPeerBroadcast;
    private TransferModeEnum _mode = TransferModeEnum.Reliable;
    private int _channel;

    private sealed record PendingPacket(ulong At, int Target, byte[] Data, TransferModeEnum Mode, int Channel);

    public SimulatedMultiplayerPeer(MultiplayerPeer inner, NetworkSimulator.Profile profile, int? randomSeed = null)
        : this(inner, _ => profile, randomSeed) { }

    /// <param name="inner">The connected transport to wrap.</param>
    /// <param name="profileFor">Conditions for packets sent to each remote peer.</param>
    /// <param name="randomSeed">A deterministic seed for tests; random by default.</param>
    public SimulatedMultiplayerPeer(
        MultiplayerPeer inner,
        Func<int, NetworkSimulator.Profile> profileFor,
        int? randomSeed = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _profileFor = profileFor ?? throw new ArgumentNullException(nameof(profileFor));
        _random = randomSeed is { } seed ? new Random(seed) : new Random();

        _inner.PeerConnected += OnPeerConnected;
        _inner.PeerDisconnected += OnPeerDisconnected;
    }

    private void OnPeerConnected(long peer)
    {
        _peers.Add((int)peer);
        EmitSignal(SignalName.PeerConnected, peer);
    }

    private void OnPeerDisconnected(long peer)
    {
        var id = (int)peer;
        _peers.Remove(id);
        _pending.RemoveAll(packet => packet.Target == id);
        _lastReliableSend.Remove(id);
        EmitSignal(SignalName.PeerDisconnected, peer);
    }

    public override int _GetUniqueId() => _inner.GetUniqueId();
    public override bool _IsServer() => _inner.GetUniqueId() == 1;
    public override bool _IsServerRelaySupported() => _inner.IsServerRelaySupported();
    public override ConnectionStatus _GetConnectionStatus() => _inner.GetConnectionStatus();
    public override int _GetMaxPacketSize() => 1 << 20;

    public override void _SetTargetPeer(int peer) => _target = peer;
    public override void _SetTransferMode(TransferModeEnum mode) => _mode = mode;
    public override TransferModeEnum _GetTransferMode() => _mode;
    public override void _SetTransferChannel(int channel) => _channel = channel;
    public override int _GetTransferChannel() => _channel;

    public override int _GetAvailablePacketCount() => _inner.GetAvailablePacketCount();
    public override int _GetPacketPeer() => _inner.GetPacketPeer();
    public override TransferModeEnum _GetPacketMode() => _inner.GetPacketMode();
    public override int _GetPacketChannel() => _inner.GetPacketChannel();
    public override byte[] _GetPacketScript() => _inner.GetPacket();

    public override Error _PutPacketScript(byte[] buffer)
    {
        if (_inner.GetConnectionStatus() != ConnectionStatus.Connected) return Error.Unconfigured;

        if (_target > 0)
            Schedule(_target, buffer);
        else
            foreach (var peer in _peers)
                if (_target == (int)TargetPeerBroadcast || peer != -_target)
                    Schedule(peer, buffer);
        return Error.Ok;
    }

    private void Schedule(int target, byte[] buffer)
    {
        var profile = _profileFor(target);
        var now = Time.GetTicksMsec();
        if (_mode != TransferModeEnum.Reliable
            && (NetworkSimulator.InLossBurst(profile, now)
                || profile.PacketLossPercent > 0 && _random.NextDouble() < profile.PacketLossPercent / 100.0))
            return;

        var jitter = profile.JitterMs <= 0
            ? 0
            : profile.JitterPeriodSeconds > 0
                ? NetworkSimulator.OscillatingJitter(profile, now)
                : _random.Next(0, profile.JitterMs + 1);
        var at = now + (ulong)Math.Max(0, profile.LatencyMs + jitter);

        // ENet's reliable stream cannot overtake itself even when individual datagrams do.
        if (_mode == TransferModeEnum.Reliable)
        {
            at = Math.Max(at, _lastReliableSend.GetValueOrDefault(target));
            _lastReliableSend[target] = at;
        }
        _pending.Add(new PendingPacket(at, target, (byte[])buffer.Clone(), _mode, _channel));
    }

    public override void _Poll()
    {
        var now = Time.GetTicksMsec();
        for (var i = 0; i < _pending.Count;)
        {
            var packet = _pending[i];
            if (packet.At > now) { i++; continue; }
            _inner.SetTargetPeer(packet.Target);
            _inner.TransferMode = packet.Mode;
            _inner.TransferChannel = packet.Channel;
            _inner.PutPacket(packet.Data);
            _pending.RemoveAt(i);
        }
        _inner.Poll();
    }

    public override void _Close()
    {
        _pending.Clear();
        _inner.Close();
    }

    public override void _DisconnectPeer(int peer, bool force) => _inner.DisconnectPeer(peer, force);
    public override bool _IsRefusingNewConnections() => _inner.RefuseNewConnections;
    public override void _SetRefuseNewConnections(bool enable) => _inner.RefuseNewConnections = enable;
}
