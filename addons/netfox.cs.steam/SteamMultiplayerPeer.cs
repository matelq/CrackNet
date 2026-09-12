using Godot;
using Netfox.Core.Serialization;

namespace Netfox.Steam;

/// <summary>
/// A Godot <see cref="MultiplayerPeer"/> over Steam's networking, written in C# rather than bridged to a GDExtension.
/// Listen-server: the host is peer 1 and relays between clients.
/// <para>
/// Assign it to <c>Multiplayer.MultiplayerPeer</c> and netfox behaves exactly as it does over ENet - it never creates
/// peers itself. Everything Steam-specific lives behind <see cref="ISteamTransport"/>.
/// </para>
/// </summary>
public partial class SteamMultiplayerPeer : MultiplayerPeerExtension
{
    /// <summary>Godot's own id for the host. Not ours to choose.</summary>
    public const int HostPeerId = 1;

    /// <summary>Broadcast to everyone; a negative target means everyone except that peer.</summary>
    private const int Broadcast = 0;

    private const byte KindData = 0;
    private const byte KindControl = 1;

    private const byte ControlWelcome = 0;   // host -> joiner: your id, then the ids already in the session
    private const byte ControlPeerJoined = 1;   // host -> everyone else: a new id
    private const byte ControlPeerLeft = 2;   // host -> everyone else: an id that left

    private readonly ISteamTransport _transport;
    private readonly bool _isHost;

    // Our peer ids are ours to assign, so they stay small and a varint header costs one byte
    private readonly Dictionary<ulong, int> _peerByConnection = new();
    private readonly Dictionary<int, ulong> _connectionByPeer = new();
    private readonly Queue<Packet> _inbox = new();
    private readonly ByteWriter _outgoing = new();

    private int _uniqueId;
    private int _nextPeerId = HostPeerId + 1;
    private ConnectionStatus _status = ConnectionStatus.Connecting;
    private bool _refusing;

    private int _target = Broadcast;
    private TransferModeEnum _mode = TransferModeEnum.Reliable;
    private int _channel;

    private readonly record struct Packet(int From, byte[] Data, TransferModeEnum Mode, int Channel);

    /// <summary>The host's peer, listening on <paramref name="transport"/>.</summary>
    public static SteamMultiplayerPeer Host(ISteamTransport transport) => new(transport, isHost: true);

    /// <summary>A client's peer. Its id is not known until the host says so, so it reports Connecting until then.</summary>
    public static SteamMultiplayerPeer Client(ISteamTransport transport) => new(transport, isHost: false);

    private SteamMultiplayerPeer(ISteamTransport transport, bool isHost)
    {
        _transport = transport;
        _isHost = isHost;

        if (isHost)
        {
            _uniqueId = HostPeerId;
            _status = ConnectionStatus.Connected;
        }

        transport.Connected += HandleConnected;
        transport.Disconnected += HandleDisconnected;
        transport.Received += HandleReceived;
    }

    /// <summary>Peer ids currently known, host included. For tests and diagnostics.</summary>
    public IReadOnlyCollection<int> KnownPeers => _connectionByPeer.Keys;

    // -- Godot's peer contract ------------------------------------------------------------------------------------

    public override int _GetUniqueId() => _uniqueId;
    public override bool _IsServer() => _isHost;
    public override ConnectionStatus _GetConnectionStatus() => _status;

    /// <summary>Clients reach each other through the host, so Godot may address packets at peers we have no link to.</summary>
    public override bool _IsServerRelaySupported() => true;

    /// <summary>Steam fragments for us; this is the ceiling it documents for a single reliable message.</summary>
    public override int _GetMaxPacketSize() => 512 * 1024;

    public override void _SetTargetPeer(int peer) => _target = peer;
    public override void _SetTransferMode(TransferModeEnum mode) => _mode = mode;
    public override TransferModeEnum _GetTransferMode() => _mode;
    public override void _SetTransferChannel(int channel) => _channel = channel;
    public override int _GetTransferChannel() => _channel;

    public override int _GetAvailablePacketCount() => _inbox.Count;
    public override int _GetPacketPeer() => _inbox.Count > 0 ? _inbox.Peek().From : 0;
    public override TransferModeEnum _GetPacketMode() => _inbox.Count > 0 ? _inbox.Peek().Mode : TransferModeEnum.Reliable;
    public override int _GetPacketChannel() => _inbox.Count > 0 ? _inbox.Peek().Channel : 0;
    public override byte[] _GetPacketScript() => _inbox.Count > 0 ? _inbox.Dequeue().Data : [];

    public override Error _PutPacketScript(byte[] buffer)
    {
        if (_status != ConnectionStatus.Connected) return Error.Unconfigured;

        // The host names the sender, so a relayed packet still says who it came from; a client names the recipient,
        // because only the host can route to anyone else
        var peerField = _isHost ? _uniqueId : _target;

        _outgoing.Clear();
        _outgoing.PutU8(KindData);
        _outgoing.PutU8(PackFlags(_mode, _channel));
        VarUint.Encode(ZigZag(peerField), _outgoing);
        _outgoing.PutData(buffer);

        var reliable = _mode == TransferModeEnum.Reliable;
        var payload = _outgoing.WrittenSpan;

        if (!_isHost)
        {
            // Everything goes to the host, which delivers or relays it
            return Send(HostPeerId, payload, reliable) ? Error.Ok : Error.Unavailable;
        }

        if (_target > 0) return Send(_target, payload, reliable) ? Error.Ok : Error.Unavailable;

        var excluded = _target < 0 ? -_target : 0;
        foreach (var peer in _connectionByPeer.Keys.ToList())
            if (peer != excluded)
                Send(peer, payload, reliable);

        return Error.Ok;
    }

    public override void _Poll() => _transport.Poll();

    public override void _Close()
    {
        _transport.Connected -= HandleConnected;
        _transport.Disconnected -= HandleDisconnected;
        _transport.Received -= HandleReceived;
        _transport.Close();

        _peerByConnection.Clear();
        _connectionByPeer.Clear();
        _inbox.Clear();
        _status = ConnectionStatus.Disconnected;
    }

    public override void _DisconnectPeer(int peer, bool force)
    {
        if (!_connectionByPeer.TryGetValue(peer, out var connection)) return;
        _transport.Disconnect(connection);
        if (!force) return;
        ForgetPeer(connection, peer);
    }

    public override bool _IsRefusingNewConnections() => _refusing;
    public override void _SetRefuseNewConnections(bool enable) => _refusing = enable;

    // -- Transport events -----------------------------------------------------------------------------------------

    private void HandleConnected(ulong connection)
    {
        if (!_isHost)
        {
            // Nothing to announce yet: a client is not a peer until the host has given it an id
            _connectionByPeer[HostPeerId] = connection;
            _peerByConnection[connection] = HostPeerId;
            return;
        }

        if (_refusing)
        {
            _transport.Disconnect(connection);
            return;
        }

        var peer = _nextPeerId++;
        _peerByConnection[connection] = peer;
        _connectionByPeer[peer] = connection;

        // The joiner needs its own id and who is already here; everyone else needs to hear about the joiner
        var welcome = new ByteWriter();
        welcome.PutU8(KindControl);
        welcome.PutU8(ControlWelcome);
        VarUint.Encode((ulong)peer, welcome);
        var others = _connectionByPeer.Keys.Where(known => known != peer).ToList();
        VarUint.Encode((ulong)others.Count, welcome);
        foreach (var other in others) VarUint.Encode((ulong)other, welcome);
        _transport.Send(connection, welcome.WrittenSpan, reliable: true);

        var joined = new ByteWriter();
        joined.PutU8(KindControl);
        joined.PutU8(ControlPeerJoined);
        VarUint.Encode((ulong)peer, joined);
        foreach (var other in others) Send(other, joined.WrittenSpan, reliable: true);

        EmitSignal(MultiplayerPeer.SignalName.PeerConnected, peer);
    }

    private void HandleDisconnected(ulong connection)
    {
        if (!_peerByConnection.TryGetValue(connection, out var peer))
        {
            if (!_isHost) _status = ConnectionStatus.Disconnected;
            return;
        }

        ForgetPeer(connection, peer);
        EmitSignal(MultiplayerPeer.SignalName.PeerDisconnected, peer);

        if (!_isHost && peer == HostPeerId)
        {
            // The host going away ends the session; there is nothing left to be connected to
            _status = ConnectionStatus.Disconnected;
            return;
        }

        if (!_isHost) return;

        var left = new ByteWriter();
        left.PutU8(KindControl);
        left.PutU8(ControlPeerLeft);
        VarUint.Encode((ulong)peer, left);
        foreach (var other in _connectionByPeer.Keys.ToList()) Send(other, left.WrittenSpan, reliable: true);
    }

    private void HandleReceived(ulong connection, byte[] data)
    {
        if (data.Length == 0) return;

        var reader = new ByteReader(data);
        var kind = reader.GetU8();

        if (kind == KindControl)
        {
            HandleControl(reader);
            return;
        }

        var flags = reader.GetU8();
        var peerField = UnZigZag(VarUint.Decode(reader));
        var payload = reader.GetPartialData(reader.AvailableBytes).ToArray();
        var (mode, channel) = UnpackFlags(flags);

        if (!_isHost)
        {
            // From the host, the peer field is whoever originally sent it
            _inbox.Enqueue(new Packet(peerField, payload, mode, channel));
            return;
        }

        if (!_peerByConnection.TryGetValue(connection, out var sender)) return;

        // On the host the peer field is where the client wanted it to go
        if (peerField == _uniqueId || peerField == Broadcast || (peerField < 0 && -peerField != _uniqueId))
            _inbox.Enqueue(new Packet(sender, payload, mode, channel));

        if (peerField == _uniqueId) return;

        Relay(sender, peerField, payload, mode, channel);
    }

    /// <summary>Forwards a client's packet to the peers it was addressed to, with the original sender in the header.</summary>
    private void Relay(int sender, int target, byte[] payload, TransferModeEnum mode, int channel)
    {
        _outgoing.Clear();
        _outgoing.PutU8(KindData);
        _outgoing.PutU8(PackFlags(mode, channel));
        VarUint.Encode(ZigZag(sender), _outgoing);
        _outgoing.PutData(payload);

        var reliable = mode == TransferModeEnum.Reliable;
        if (target > 0)
        {
            if (target != _uniqueId) Send(target, _outgoing.WrittenSpan, reliable);
            return;
        }

        var excluded = target < 0 ? -target : 0;
        foreach (var peer in _connectionByPeer.Keys.ToList())
            if (peer != sender && peer != excluded)
                Send(peer, _outgoing.WrittenSpan, reliable);
    }

    private void HandleControl(ByteReader reader)
    {
        var control = reader.GetU8();
        switch (control)
        {
            case ControlWelcome:
                _uniqueId = (int)VarUint.Decode(reader);
                var count = (int)VarUint.Decode(reader);
                _status = ConnectionStatus.Connected;

                // Godot expects the host among the peers a client knows about, and it arrives in this list
                for (var i = 0; i < count; i++)
                {
                    var peer = (int)VarUint.Decode(reader);
                    if (peer != _uniqueId) EmitSignal(MultiplayerPeer.SignalName.PeerConnected, peer);
                }
                break;

            case ControlPeerJoined:
                var joined = (int)VarUint.Decode(reader);
                if (joined != _uniqueId) EmitSignal(MultiplayerPeer.SignalName.PeerConnected, joined);
                break;

            case ControlPeerLeft:
                var left = (int)VarUint.Decode(reader);
                if (left != _uniqueId) EmitSignal(MultiplayerPeer.SignalName.PeerDisconnected, left);
                break;
        }
    }

    // -- Plumbing -------------------------------------------------------------------------------------------------

    private bool Send(int peer, ReadOnlySpan<byte> payload, bool reliable)
        => _connectionByPeer.TryGetValue(peer, out var connection) && _transport.Send(connection, payload, reliable);

    private void ForgetPeer(ulong connection, int peer)
    {
        _peerByConnection.Remove(connection);
        _connectionByPeer.Remove(peer);
    }

    /// <summary>Transfer mode in the low two bits, channel in the rest. Channels above 63 fold back to 0.</summary>
    private static byte PackFlags(TransferModeEnum mode, int channel)
        => (byte)(((int)mode & 0x3) | ((channel & 0x3F) << 2));

    private static (TransferModeEnum Mode, int Channel) UnpackFlags(byte flags)
        => ((TransferModeEnum)(flags & 0x3), (flags >> 2) & 0x3F);

    /// <summary>Peer ids are signed - a negative target excludes that peer - so they zigzag before the varint.</summary>
    private static ulong ZigZag(int value) => (ulong)((value << 1) ^ (value >> 31));

    private static int UnZigZag(ulong value) => (int)(value >> 1) ^ -(int)(value & 1);
}
