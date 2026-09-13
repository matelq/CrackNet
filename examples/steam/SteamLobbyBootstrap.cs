using Godot;

namespace Netfox.Examples.Steam;

/// <summary>
/// Steam as netfox's transport: create or join a lobby, hand the resulting peer to Godot, and netfox behaves exactly
/// as it does over ENet - it never creates peers itself.
/// <para>
/// Driven through <c>ClassDB</c> and the <c>Steam</c> singleton rather than through C# bindings, the same way
/// <c>RapierPhysicsDriver3D</c> drives the Rapier extension. GodotSteam registers <c>Steam</c>,
/// <c>SteamMultiplayerPeer</c> and <c>SteamPacketPeer</c>, and that is all we need - so there is no dependency on
/// GodotSteam_CSharpBindings, whose last release predates the extension by two years.
/// </para>
/// <para>
/// Setup: unzip the GodotSteam GDExtension (<c>v4.22.1-gde</c> or newer) into <c>addons/godotsteam</c>, put
/// <c>steam_appid.txt</c> next to the executable - 480, Spacewar, for testing - and run with the Steam client open.
/// </para>
/// </summary>
[GlobalClass]
public partial class SteamLobbyBootstrap : Node
{
    /// <summary>480 is Valve's Spacewar, the app id to develop against before you have your own.</summary>
    [Export] public uint AppId { get; set; } = 480;

    [Export] public int MaxPlayers { get; set; } = 4;

    /// <summary>Matches GodotSteam's LOBBY_TYPE_ enum: 0 private, 1 friends only, 2 public.</summary>
    [Export] public int LobbyType { get; set; } = 1;

    public ulong LobbyId { get; private set; }

    /// <summary>The Steam peer, once a lobby is up. It is a MultiplayerPeerExtension, so Godot takes it as it is.</summary>
    public MultiplayerPeer? Peer { get; private set; }

    /// <summary>Raised once the lobby exists and the peer is assigned. Carries the lobby id, to share or to show.</summary>
    public event Action<ulong>? LobbyReady;

    /// <summary>Raised with a readable reason when Steam, the lobby or the peer could not be brought up.</summary>
    public event Action<string>? Failed;

    private GodotObject? _steam;

    /// <summary>True when the GodotSteam extension is installed. Everything here is a no-op without it.</summary>
    public static bool IsAvailable
        => Engine.HasSingleton("Steam") && ClassDB.ClassExists("SteamMultiplayerPeer");

    public override void _Ready()
    {
        if (!IsAvailable)
        {
            Fail("GodotSteam is not installed: no Steam singleton and no SteamMultiplayerPeer class");
            return;
        }

        _steam = Engine.GetSingleton("Steam");

        var init = _steam.Call("steamInitEx", AppId, false).AsGodotDictionary();
        var status = init["status"].AsInt32();
        if (status != 0)
        {
            Fail($"Steam init failed ({status}): {init["verbal"].AsString()}");
            return;
        }

        _steam.Connect("lobby_created", Callable.From<long, ulong>(HandleLobbyCreated));
        _steam.Connect("lobby_joined", Callable.From<ulong, long, bool, long>(HandleLobbyJoined));
    }

    /// <summary>Steam's callbacks are pumped by hand, the way its API expects.</summary>
    public override void _Process(double delta) => _steam?.Call("run_callbacks");

    /// <summary>Creates a lobby and hosts inside it. The lobby owner is the host, and netfox's peer 1.</summary>
    public void Host() => _steam?.Call("createLobby", LobbyType, MaxPlayers);

    /// <summary>Joins an existing lobby; its owner is the host.</summary>
    public void Join(ulong lobbyId) => _steam?.Call("joinLobby", lobbyId);

    private void HandleLobbyCreated(long connect, ulong lobbyId)
    {
        if (connect != 1)
        {
            Fail($"Lobby creation failed: {connect}");
            return;
        }

        LobbyId = lobbyId;
        _steam?.Call("setLobbyJoinable", lobbyId, true);

        // host_with_lobby creates the host peer and connects to anyone already in the lobby
        Assign("host_with_lobby", lobbyId);
    }

    private void HandleLobbyJoined(ulong lobbyId, long permissions, bool locked, long response)
    {
        const long enterSuccess = 1;
        if (response != enterSuccess)
        {
            Fail($"Joining the lobby failed: {response}");
            return;
        }

        LobbyId = lobbyId;

        // The owner already built its peer in HandleLobbyCreated
        if (Peer is not null) return;

        Assign("connect_to_lobby", lobbyId);
    }

    private void Assign(string method, ulong lobbyId)
    {
        if (ClassDB.Instantiate("SteamMultiplayerPeer").AsGodotObject() is not MultiplayerPeer peer)
        {
            Fail("SteamMultiplayerPeer did not instantiate as a MultiplayerPeer");
            return;
        }

        // Nagle holds a small message back for a few milliseconds hoping more shows up, then sends them together. A
        // tick-based netcode is exactly the program it was written to punish: netfox sends once per tick on purpose,
        // so every message here is what Valve's own docs name as the proper case for turning it off - "flushing the
        // last message in a server tick". Left on, it adds latency to the input every peer is waiting for, which
        // input_delay then compensates for.
        //
        // no_delay stays off, and not by oversight. GodotSteam applies it per connection, to reliable messages too
        // (_get_steam_packet_flags), and Steam says it is invalid for reliable messages: a message that cannot go out
        // within ~200ms is dropped rather than queued. Right for per-tick state, wrong for the identity handshake.
        // The same function maps UnreliableOrdered onto Reliable, so netfox's sync-state channel is reliable here.
        peer.Set("no_nagle", true);

        var error = (Error)peer.Call(method, lobbyId).AsInt32();
        if (error != Error.Ok)
        {
            Fail($"{method} failed: {error}");
            peer.Dispose();
            return;
        }

        Peer = peer;

        // From here netfox is on its own: NetworkEvents starts the tick loop with the session, exactly as over ENet
        Multiplayer.MultiplayerPeer = peer;
        LobbyReady?.Invoke(lobbyId);
    }

    private void Fail(string reason)
    {
        GD.PushError($"SteamLobbyBootstrap: {reason}");
        Failed?.Invoke(reason);
    }
}
