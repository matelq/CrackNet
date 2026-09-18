using Godot;

namespace CrackNet.Examples.Steam;

/// <summary>
/// #6, the half that needs no second account: does GodotSteam load into this Godot, are the classes we drive through
/// ClassDB actually there, and do the calls we make have the shapes we think they do.
/// <para>
/// Reports and exits 0 when the extension is missing, so it is safe to keep and safe to run anywhere. With the
/// extension present but no Steam client running, initialisation fails on purpose - and the failure is itself the
/// evidence that the call signature is right.
/// </para>
/// <para>
/// Run: <c>godot --headless --path . res://examples/steam/SteamSmoke.tscn</c>
/// </para>
/// </summary>
public partial class SteamSmoke : Node
{
    public override void _Ready()
    {
        var hasSingleton = Engine.HasSingleton("Steam");
        var hasPeer = ClassDB.ClassExists("SteamMultiplayerPeer");
        var hasPacketPeer = ClassDB.ClassExists("SteamPacketPeer");

        GD.Print($"STEAM SMOKE extension={hasSingleton && hasPeer} singleton={hasSingleton} " +
                 $"peer_class={hasPeer} packet_peer_class={hasPacketPeer}");

        if (!hasSingleton || !hasPeer)
        {
            GD.Print("STEAM SMOKE skipped=True reason=godotsteam-not-installed");
            GetTree().Quit(0);
            return;
        }

        // The methods we call through ClassDB, checked against the class rather than against a binding library
        foreach (var method in new[] { "steamInitEx", "run_callbacks", "createLobby", "joinLobby", "setLobbyJoinable", "getSteamID" })
            GD.Print($"STEAM SMOKE Steam.{method}={ClassDB.ClassHasMethod("Steam", method, true)}");
        foreach (var method in new[] { "host_with_lobby", "connect_to_lobby", "create_host", "create_client" })
            GD.Print($"STEAM SMOKE SteamMultiplayerPeer.{method}={ClassDB.ClassHasMethod("SteamMultiplayerPeer", method, true)}");

        // The cast the bootstrap depends on: Godot only accepts a MultiplayerPeer for Multiplayer.MultiplayerPeer
        var instance = ClassDB.Instantiate("SteamMultiplayerPeer").AsGodotObject();
        var isMultiplayerPeer = instance is MultiplayerPeer;
        GD.Print($"STEAM SMOKE instantiates_as_multiplayer_peer={isMultiplayerPeer} type={instance?.GetType().Name}");
        instance?.Dispose();

        // Without a Steam client this fails, which still tells us the signature and the result shape are right
        var steam = Engine.GetSingleton("Steam");
        var init = steam.Call("steamInitEx", 480u, false).AsGodotDictionary();
        GD.Print($"STEAM SMOKE init_status={init["status"]} init_verbal={init["verbal"]}");

        GD.Print($"STEAM SMOKE skipped=False ok={isMultiplayerPeer}");
        GetTree().Quit(isMultiplayerPeer ? 0 : 1);
    }
}
