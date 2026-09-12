namespace Netfox.Steam;

/// <summary>
/// The connection-level operations <see cref="SteamMultiplayerPeer"/> needs from Steam, and the only part of it that
/// touches Steamworks.
/// <para>
/// Everything above this - peer ids, the join handshake, relaying, transfer modes and channels - is ordinary logic
/// that a fake transport can exercise, which is what makes the peer testable without a Steam client, an account, or
/// a second machine. <see cref="SteamNetworkingTransport"/> is the real one.
/// </para>
/// </summary>
public interface ISteamTransport
{
    /// <summary>A connection appeared. The handle is this transport's own; the peer only passes it back.</summary>
    event Action<ulong>? Connected;

    /// <summary>A connection went away, for any reason.</summary>
    event Action<ulong>? Disconnected;

    /// <summary>Bytes arrived on a connection. The array belongs to the callee for the duration of the call only.</summary>
    event Action<ulong, byte[]>? Received;

    /// <summary>True once this side is ready to carry traffic: the socket is listening, or the connection is up.</summary>
    bool IsReady { get; }

    /// <summary>Sends to one connection. Returns false when the connection is gone or the send failed.</summary>
    bool Send(ulong connection, ReadOnlySpan<byte> data, bool reliable);

    /// <summary>Drops one connection.</summary>
    void Disconnect(ulong connection);

    /// <summary>Pumps callbacks and raises the events above. Called once per Godot poll.</summary>
    void Poll();

    /// <summary>Tears the socket or connection down.</summary>
    void Close();
}
