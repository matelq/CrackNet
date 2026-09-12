// The only file in this addon that references Steamworks. Compiled with NETFOX_STEAM defined:
//
//   <PropertyGroup>
//     <DefineConstants>$(DefineConstants);NETFOX_STEAM</DefineConstants>
//   </PropertyGroup>
//   <ItemGroup>
//     <PackageReference Include="Facepunch.Steamworks" Version="2.3.3" />   <!-- Windows; see the README for Posix -->
//   </ItemGroup>
//
// Without the define, SteamMultiplayerPeer and its tests still compile and run against any ISteamTransport, which is
// how the peer is covered without a Steam client.
#if NETFOX_STEAM
using Steamworks;
using Steamworks.Data;

namespace Netfox.Steam;

/// <summary>
/// <see cref="ISteamTransport"/> over Steam's relay sockets: no ports, no NAT punchthrough, no IP addresses handed
/// between players - Valve's relay carries it, and peers know each other only by Steam id.
/// <para>
/// Create one with <see cref="Host"/> or <see cref="Join"/>, hand it to <see cref="SteamMultiplayerPeer"/>, and
/// assign that to <c>Multiplayer.MultiplayerPeer</c>. <c>SteamClient.Init</c> is the caller's to do.
/// </para>
/// </summary>
public sealed class SteamNetworkingTransport : ISteamTransport
{
    /// <summary>Steam's own multiplexing slot. Only matters if one app opens several unrelated sockets.</summary>
    public const int DefaultVirtualPort = 0;

    private SocketManager? _socket;
    private ConnectionManager? _connection;

    public event Action<ulong>? Connected;
    public event Action<ulong>? Disconnected;
    public event Action<ulong, byte[]>? Received;

    public bool IsReady => _socket is not null || _connection is { Connected: true };

    /// <summary>Opens a relay socket others can reach by this user's Steam id.</summary>
    public static SteamNetworkingTransport Host(int virtualPort = DefaultVirtualPort)
    {
        var transport = new SteamNetworkingTransport();
        transport._socket = SteamNetworkingSockets.CreateRelaySocket<SocketManager>(virtualPort);
        transport._socket.Interface = new SocketBridge(transport);
        return transport;
    }

    /// <summary>Connects to a host by Steam id, through the relay.</summary>
    public static SteamNetworkingTransport Join(SteamId host, int virtualPort = DefaultVirtualPort)
    {
        var transport = new SteamNetworkingTransport();
        transport._connection = SteamNetworkingSockets.ConnectRelay<ConnectionManager>(host, virtualPort);
        transport._connection.Interface = new ConnectionBridge(transport);
        return transport;
    }

    private SteamNetworkingTransport() { }

    public bool Send(ulong connection, ReadOnlySpan<byte> data, bool reliable)
    {
        var sendType = reliable ? SendType.Reliable : SendType.Unreliable | SendType.NoNagle;

        // SendMessage takes a byte[]; the copy is the price of the managed boundary and is what the issue's
        // "measure the copy overhead" item is about
        var buffer = data.ToArray();
        var target = new Connection { Id = (uint)connection };
        return target.SendMessage(buffer, sendType) == Result.OK;
    }

    public void Disconnect(ulong connection)
        => new Connection { Id = (uint)connection }.Close(linger: false, 0, "Disconnected by peer");

    public void Poll()
    {
        // Steam's callbacks drive connection state; Receive drains queued messages into the bridges below
        SteamClient.RunCallbacks();
        _socket?.Receive();
        _connection?.Receive();
    }

    public void Close()
    {
        _socket?.Close();
        _connection?.Close();
    }

    private void RaiseConnected(uint connection) => Connected?.Invoke(connection);
    private void RaiseDisconnected(uint connection) => Disconnected?.Invoke(connection);

    private void RaiseReceived(uint connection, IntPtr data, int size)
    {
        if (size <= 0) return;
        var payload = new byte[size];
        System.Runtime.InteropServices.Marshal.Copy(data, payload, 0, size);
        Received?.Invoke(connection, payload);
    }

    /// <summary>Host side: Steam's socket callbacks, forwarded as transport events.</summary>
    private sealed class SocketBridge(SteamNetworkingTransport transport) : ISocketManager
    {
        private readonly SteamNetworkingTransport _transport = transport;

        public void OnConnecting(Connection connection, ConnectionInfo info) => connection.Accept();

        public void OnConnected(Connection connection, ConnectionInfo info) => _transport.RaiseConnected(connection.Id);

        public void OnDisconnected(Connection connection, ConnectionInfo info) => _transport.RaiseDisconnected(connection.Id);

        public void OnMessage(Connection connection, NetIdentity identity, IntPtr data, int size, long messageNum, long recvTime, int channel)
            => _transport.RaiseReceived(connection.Id, data, size);
    }

    /// <summary>Client side: one connection, to the host.</summary>
    private sealed class ConnectionBridge(SteamNetworkingTransport transport) : IConnectionManager
    {
        private readonly SteamNetworkingTransport _transport = transport;

        public void OnConnecting(ConnectionInfo info) { }

        public void OnConnected(ConnectionInfo info)
            => _transport.RaiseConnected(_transport._connection!.Connection.Id);

        public void OnDisconnected(ConnectionInfo info)
            => _transport.RaiseDisconnected(_transport._connection!.Connection.Id);

        public void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
            => _transport.RaiseReceived(_transport._connection!.Connection.Id, data, size);
    }
}
#endif
