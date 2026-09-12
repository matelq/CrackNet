// Steam transport bootstrap for netfox.cs, built on GodotSteam (GDExtension) + LauraWebdev/GodotSteam_CSharpBindings.
//
// Setup:
//   1. Install GodotSteam GDExtension 4.22+ into addons/godotsteam and the C# bindings into addons/godotsteam_csharpbindings.
//   2. Put steam_appid.txt next to the executable (480 for testing).
//   3. Define GODOTSTEAM in the csproj: <DefineConstants>$(DefineConstants);GODOTSTEAM</DefineConstants>
//   4. Add this node to your lobby scene and call Host() or Join(lobbyId).
//
// netfox.cs never creates peers itself: once Multiplayer.MultiplayerPeer is assigned, NetworkEvents starts NetworkTime
// on the host immediately and on clients after connected_to_server, exactly as with ENet.
//
// NOTE: this file is a reference implementation compiled only with GODOTSTEAM defined; it has not been exercised
// against a live Steam client in this repository.
#if GODOTSTEAM
using Godot;
using GodotSteam;

namespace Netfox.Examples.Steam;

public partial class SteamLobbyBootstrap : Node
{
    [Export] public uint AppId { get; set; } = 480;
    [Export] public int MaxPlayers { get; set; } = 4;

    public ulong LobbyId { get; private set; }
    public SteamMultiplayerPeer? Peer { get; private set; }

    public event Action<ulong>? LobbyReady;

    public override void _Ready()
    {
        var init = Steam.SteamInitEx(AppId, true);
        if (init.Status != Steam.SteamAPIInitResult.Ok)
        {
            GD.PushError($"Steam init failed: {init.Verbal}");
            return;
        }

        Steam.LobbyCreated += OnLobbyCreated;
        Steam.LobbyJoined += OnLobbyJoined;
    }

    public override void _Process(double delta) => Steam.RunCallbacks();

    /// <summary>Create a friends-only lobby and host the game inside it. Peer id 1, authority over the world.</summary>
    public void Host() => Steam.CreateLobby(Steam.LobbyType.FriendsOnly, MaxPlayers);

    /// <summary>Join an existing lobby; the lobby owner is the host.</summary>
    public void Join(ulong lobbyId) => Steam.JoinLobby(lobbyId);

    private void OnLobbyCreated(long connect, ulong lobbyId)
    {
        if (connect != 1)
        {
            GD.PushError($"Lobby creation failed: {connect}");
            return;
        }

        LobbyId = lobbyId;
        Steam.SetLobbyJoinable(lobbyId, true);

        Peer = SteamMultiplayerPeer.Instantiate();
        var error = Peer.CreateHost(0);
        if (error != Error.Ok)
        {
            GD.PushError($"SteamMultiplayerPeer.CreateHost failed: {error}");
            return;
        }

        Multiplayer.MultiplayerPeer = Peer;
        LobbyReady?.Invoke(lobbyId);
    }

    private void OnLobbyJoined(ulong lobbyId, long permissions, bool locked, long response)
    {
        if (response != (long)Steam.ChatRoomEnterResponse.Success)
        {
            GD.PushError($"Joining lobby failed: {response}");
            return;
        }

        LobbyId = lobbyId;
        var hostId = Steam.GetLobbyOwner(lobbyId);
        if (hostId == Steam.GetSteamID()) return; // We are the host, peer already created

        Peer = SteamMultiplayerPeer.Instantiate();
        var error = Peer.CreateClient(hostId, 0);
        if (error != Error.Ok)
        {
            GD.PushError($"SteamMultiplayerPeer.CreateClient failed: {error}");
            return;
        }

        Multiplayer.MultiplayerPeer = Peer;
        LobbyReady?.Invoke(lobbyId);
    }
}
#endif
