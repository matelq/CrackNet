using Godot;
using Netfox.Steam;

namespace Netfox.Tests;

/// <summary>
/// Stands in for Steam's networking: connections are handles in a dictionary and a send is a direct call into the
/// other side. Everything <see cref="SteamMultiplayerPeer"/> does above the transport - ids, the join handshake,
/// relaying, modes and channels - is exercised through this, so none of it waits on a Steam client.
/// </summary>
public sealed class FakeSteamNetwork
{
    // A link is two handles, one per side: sending on yours arrives on theirs
    private readonly Dictionary<ulong, (FakeSteamTransport Target, ulong TargetHandle)> _links = new();
    private ulong _nextHandle = 1;

    /// <summary>Bytes handed to the network per sending handle, before anything is delivered.</summary>
    public Dictionary<ulong, long> Traffic { get; } = new();

    public FakeSteamTransport CreateTransport() => new(this);

    /// <summary>Links two transports, the way a relay connection coming up does.</summary>
    public void Connect(FakeSteamTransport a, FakeSteamTransport b)
    {
        var forA = _nextHandle++;
        var forB = _nextHandle++;

        _links[forA] = (b, forB);
        _links[forB] = (a, forA);

        a.Link(forA);
        b.Link(forB);

        a.RaiseConnected(forA);
        b.RaiseConnected(forB);
    }

    internal bool Deliver(ulong from, byte[] data)
    {
        Traffic[from] = Traffic.GetValueOrDefault(from) + data.Length;
        if (!_links.TryGetValue(from, out var link)) return false;
        link.Target.Enqueue(link.TargetHandle, data);
        return true;
    }

    internal void Drop(ulong handle)
    {
        if (!_links.Remove(handle, out var link)) return;
        _links.Remove(link.TargetHandle);
        link.Target.RaiseDisconnected(link.TargetHandle);
    }
}

/// <summary>One side of a <see cref="FakeSteamNetwork"/>.</summary>
public sealed class FakeSteamTransport : ISteamTransport
{
    private readonly FakeSteamNetwork _network;
    private readonly HashSet<ulong> _handles = new();
    private readonly List<(ulong Connection, byte[] Data)> _pending = new();

    internal FakeSteamTransport(FakeSteamNetwork network) => _network = network;

    public event Action<ulong>? Connected;
    public event Action<ulong>? Disconnected;
    public event Action<ulong, byte[]>? Received;

    public bool IsReady { get; private set; } = true;

    internal void Link(ulong handle) => _handles.Add(handle);

    internal void RaiseConnected(ulong handle) => Connected?.Invoke(handle);

    internal void RaiseDisconnected(ulong handle)
    {
        if (_handles.Remove(handle)) Disconnected?.Invoke(handle);
    }

    internal void Enqueue(ulong handle, byte[] data) => _pending.Add((handle, data));

    public bool Send(ulong connection, ReadOnlySpan<byte> data, bool reliable)
        => _handles.Contains(connection) && _network.Deliver(connection, data.ToArray());

    public void Disconnect(ulong connection)
    {
        if (!_handles.Remove(connection)) return;
        _network.Drop(connection);
        Disconnected?.Invoke(connection);
    }

    public void Poll()
    {
        if (_pending.Count == 0) return;
        var batch = _pending.ToList();
        _pending.Clear();
        foreach (var (connection, data) in batch) Received?.Invoke(connection, data);
    }

    public void Close()
    {
        IsReady = false;
        foreach (var handle in _handles.ToList()) _network.Drop(handle);
        _handles.Clear();
        _pending.Clear();
    }
}

/// <summary>#9: the Steam peer's logic, without Steam.</summary>
public partial class SteamPeerTests : TestSuite
{
    private FakeSteamNetwork _network = null!;
    private SteamMultiplayerPeer _host = null!;
    private FakeSteamTransport _hostTransport = null!;

    public override Task BeforeCase()
    {
        _network = new FakeSteamNetwork();
        _hostTransport = _network.CreateTransport();
        _host = SteamMultiplayerPeer.Host(_hostTransport);
        return Task.CompletedTask;
    }

    public override Task AfterCase()
    {
        _host.Free();
        return Task.CompletedTask;
    }

    /// <summary>Connects a client and pumps until its id has arrived.</summary>
    private SteamMultiplayerPeer Join()
    {
        var transport = _network.CreateTransport();
        var client = SteamMultiplayerPeer.Client(transport);
        _network.Connect(_hostTransport, transport);
        Pump(client);
        return client;
    }

    private void Pump(params SteamMultiplayerPeer[] clients)
    {
        // A join takes a round trip: the client's link comes up, the host answers, the client reads the answer
        for (var i = 0; i < 4; i++)
        {
            _host._Poll();
            foreach (var client in clients) client._Poll();
        }
    }

    [Test]
    public void HostIsPeerOneAndConnectedImmediately()
    {
        Expect.Equal(SteamMultiplayerPeer.HostPeerId, _host._GetUniqueId());
        Expect.True(_host._IsServer());
        Expect.Equal(MultiplayerPeer.ConnectionStatus.Connected, _host._GetConnectionStatus());
    }

    [Test]
    public void ClientLearnsItsIdFromTheHost()
    {
        var client = SteamMultiplayerPeer.Client(_network.CreateTransport());
        Expect.Equal(MultiplayerPeer.ConnectionStatus.Connecting, client._GetConnectionStatus());
        client.Free();

        var joined = Join();
        Expect.Equal(2, joined._GetUniqueId());
        Expect.Equal(MultiplayerPeer.ConnectionStatus.Connected, joined._GetConnectionStatus());
        Expect.False(joined._IsServer());
        joined.Free();
    }

    [Test]
    public void PeersAreAnnouncedInBothDirections()
    {
        var hostSaw = new List<long>();
        _host.Connect(MultiplayerPeer.SignalName.PeerConnected, Callable.From<long>(hostSaw.Add));

        var first = Join();
        var firstSaw = new List<long>();
        first.Connect(MultiplayerPeer.SignalName.PeerConnected, Callable.From<long>(firstSaw.Add));

        var second = Join();
        Pump(first, second);

        Expect.SequenceEqual([2L, 3L], hostSaw);
        Expect.SequenceEqual([3L], firstSaw);

        // The joiner hears about everyone already in the session, the host included
        Expect.True(second.KnownPeers.Contains(SteamMultiplayerPeer.HostPeerId), "a client has to know the host");

        first.Free();
        second.Free();
    }

    [Test]
    public void PacketsTravelBothWays()
    {
        var client = Join();

        _host._SetTargetPeer(2);
        _host._SetTransferMode(MultiplayerPeer.TransferModeEnum.Reliable);
        Expect.Equal(Error.Ok, _host._PutPacketScript([1, 2, 3]));
        Pump(client);

        Expect.Equal(1, client._GetAvailablePacketCount());
        Expect.Equal(SteamMultiplayerPeer.HostPeerId, client._GetPacketPeer());
        Expect.SequenceEqual<byte>([1, 2, 3], client._GetPacketScript());

        client._SetTargetPeer(SteamMultiplayerPeer.HostPeerId);
        Expect.Equal(Error.Ok, client._PutPacketScript([9, 8]));
        Pump(client);

        Expect.Equal(1, _host._GetAvailablePacketCount());
        Expect.Equal(2, _host._GetPacketPeer());
        Expect.SequenceEqual<byte>([9, 8], _host._GetPacketScript());

        client.Free();
    }

    [Test]
    public void ModeAndChannelSurviveTheTrip()
    {
        var client = Join();

        _host._SetTargetPeer(2);
        _host._SetTransferMode(MultiplayerPeer.TransferModeEnum.UnreliableOrdered);
        _host._SetTransferChannel(5);
        _host._PutPacketScript([42]);
        Pump(client);

        Expect.Equal(MultiplayerPeer.TransferModeEnum.UnreliableOrdered, client._GetPacketMode());
        Expect.Equal(5, client._GetPacketChannel());
        client.Free();
    }

    /// <summary>Clients have no link to each other, so the host has to forward - and say who it came from.</summary>
    [Test]
    public void HostRelaysBetweenClients()
    {
        var first = Join();
        var second = Join();
        Pump(first, second);

        first._SetTargetPeer(3);
        first._SetTransferMode(MultiplayerPeer.TransferModeEnum.Reliable);
        first._PutPacketScript([7]);
        Pump(first, second);

        Expect.Equal(0, _host._GetAvailablePacketCount(), "a packet addressed elsewhere is not for the host");
        Expect.Equal(1, second._GetAvailablePacketCount());
        Expect.Equal(2, second._GetPacketPeer(), "the relayed packet has to name the original sender");
        Expect.SequenceEqual<byte>([7], second._GetPacketScript());

        first.Free();
        second.Free();
    }

    [Test]
    public void BroadcastFromAClientReachesTheHostAndTheOthers()
    {
        var first = Join();
        var second = Join();
        Pump(first, second);

        first._SetTargetPeer(0);
        first._PutPacketScript([1]);
        Pump(first, second);

        Expect.Equal(1, _host._GetAvailablePacketCount(), "a broadcast includes the host");
        Expect.Equal(2, _host._GetPacketPeer());
        Expect.Equal(1, second._GetAvailablePacketCount());
        Expect.Equal(2, second._GetPacketPeer());
        Expect.Equal(0, first._GetAvailablePacketCount(), "a broadcast does not come back to its sender");

        first.Free();
        second.Free();
    }

    [Test]
    public void BroadcastCanExcludeOnePeer()
    {
        var first = Join();
        var second = Join();
        Pump(first, second);

        // Godot asks for "everyone except N" with a negative target
        _host._SetTargetPeer(-2);
        _host._PutPacketScript([5]);
        Pump(first, second);

        Expect.Equal(0, first._GetAvailablePacketCount(), "peer 2 was excluded");
        Expect.Equal(1, second._GetAvailablePacketCount());

        first.Free();
        second.Free();
    }

    [Test]
    public void LosingAClientTellsEveryoneElse()
    {
        var first = Join();
        var second = Join();
        Pump(first, second);

        var hostSaw = new List<long>();
        var secondSaw = new List<long>();
        _host.Connect(MultiplayerPeer.SignalName.PeerDisconnected, Callable.From<long>(hostSaw.Add));
        second.Connect(MultiplayerPeer.SignalName.PeerDisconnected, Callable.From<long>(secondSaw.Add));

        first._Close();
        Pump(first, second);

        Expect.SequenceEqual([2L], hostSaw);
        Expect.SequenceEqual([2L], secondSaw);
        Expect.False(_host.KnownPeers.Contains(2), "the host should have forgotten the peer");

        first.Free();
        second.Free();
    }

    [Test]
    public void LosingTheHostDisconnectsTheClient()
    {
        var client = Join();
        Expect.Equal(MultiplayerPeer.ConnectionStatus.Connected, client._GetConnectionStatus());

        _host._Close();
        Pump(client);

        Expect.Equal(MultiplayerPeer.ConnectionStatus.Disconnected, client._GetConnectionStatus());
        client.Free();
    }

    [Test]
    public void RefusingConnectionsDropsTheJoiner()
    {
        _host._SetRefuseNewConnections(true);
        Expect.True(_host._IsRefusingNewConnections());

        var transport = _network.CreateTransport();
        var client = SteamMultiplayerPeer.Client(transport);
        _network.Connect(_hostTransport, transport);
        Pump(client);

        Expect.Empty(_host.KnownPeers);
        Expect.NotEqual(MultiplayerPeer.ConnectionStatus.Connected, client._GetConnectionStatus());
        client.Free();
    }

    /// <summary>The header is what every packet pays for, so it is worth knowing what it costs.</summary>
    [Test]
    public void HeaderCostsThreeBytesForASmallSession()
    {
        var client = Join();
        _network.Traffic.Clear();

        _host._SetTargetPeer(2);
        _host._SetTransferChannel(1);
        var payload = new byte[40];
        _host._PutPacketScript(payload);

        var sent = _network.Traffic.Values.Sum();
        GD.Print($"STEAM HEADER {payload.Length} byte payload went out as {sent} bytes");
        Expect.Equal(payload.Length + 3, (int)sent,
            $"kind, flags and a one byte peer id: {sent} bytes for a {payload.Length} byte payload");

        client.Free();
    }
}
