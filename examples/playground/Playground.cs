using Godot;
using Netfox.Extras;

namespace Netfox.Examples.Playground;

/// <summary>
/// The sample's lobby and spawner: host or join by address, then one player per peer.
/// <para>
/// Everything netfox needs is in the scene rather than here - the synchronizers, their property paths, the
/// interpolator. This script only sets up the peer and puts players in the world, which is the division a game would
/// use too.
/// </para>
/// </summary>
public partial class Playground : Node3D
{
    [Export] public PackedScene PlayerScene { get; set; } = null!;
    [Export] public Node3D SpawnRoot { get; set; } = null!;
    [Export] public Node3D ProjectileRoot { get; set; } = null!;
    [Export] public int Port { get; set; } = 9999;

    private PhysicsTier _physics = null!;
    private Scoreboard _scoreboard = null!;
    private Steam.SteamLobbyBootstrap? _steam;
    private LineEdit _address = null!;
    private Control _lobby = null!;
    private Label _status = null!;
    private readonly Dictionary<int, Node3D> _players = new();

    public override void _Ready()
    {
        _physics = GetNode<PhysicsTier>("PhysicsTier");
        _scoreboard = GetNode<Scoreboard>("World/Scoreboard");
        _lobby = GetNode<Control>("UI/Lobby");
        _address = GetNode<LineEdit>("UI/Lobby/Panel/Rows/Address");
        _status = GetNode<Label>("UI/Status");

        GetNode<Button>("UI/Lobby/Panel/Rows/Buttons/Host").Pressed += Host;
        GetNode<Button>("UI/Lobby/Panel/Rows/Buttons/Join").Pressed += Join;

        // The Steam tier: present only when the GodotSteam extension is installed, so the button says so otherwise
        var steamButton = GetNode<Button>("UI/Lobby/Panel/Rows/Buttons/Steam");
        if (Steam.SteamLobbyBootstrap.IsAvailable)
        {
            _steam = new Steam.SteamLobbyBootstrap { Name = "SteamLobbyBootstrap" };
            _steam.Failed += reason => _status.Text = $"Steam: {reason}";
            _steam.LobbyReady += lobby => _status.Text = $"Steam lobby {lobby} - share this to be joined";
            AddChild(_steam);
            // A lobby id in the address field joins that lobby; anything else hosts a new one
            steamButton.Pressed += () =>
            {
                _lobby.Hide();
                if (ulong.TryParse(_address.Text, out var lobby)) _steam.Join(lobby);
                else _steam.Host();
            };
        }
        else
        {
            steamButton.Disabled = true;
            steamButton.TooltipText = "GodotSteam is not installed";
        }

        GetNode<Beacon>("World/Beacon").PlayerRoot = SpawnRoot;

        // NetworkEvents starts and stops the tick loop with the session, so nothing here has to
        NetworkEvents.Instance.OnServerStart += HandleServerStart;
        NetworkEvents.Instance.OnClientStart += HandleClientStart;
        NetworkEvents.Instance.OnPeerJoin += SpawnPlayer;
        NetworkEvents.Instance.OnPeerLeave += DespawnPlayer;

        // The NetworkSimulator in the scene connects the editor's extra instances on its own when autoconnect is on
        // (Project Settings > Netfox > Autoconnect). It also hosts the latency and loss proxy configured there.
        var simulator = GetNodeOrNull<NetworkSimulator>("NetworkSimulator");
        if (simulator is not null)
        {
            simulator.ServerCreated += HandleServerStart;
            simulator.ClientConnected += () => HandleClientStart(Multiplayer.GetUniqueId());
        }
    }

    public override void _ExitTree()
    {
        if (NetworkEvents.Instance is not { } events) return;
        events.OnServerStart -= HandleServerStart;
        events.OnClientStart -= HandleClientStart;
        events.OnPeerJoin -= SpawnPlayer;
        events.OnPeerLeave -= DespawnPlayer;
    }

    public override void _Process(double delta)
    {
        if (!NetworkTime.Instance.IsInitialSyncDone()) return;

        var rollback = NetworkRollback.Instance;
        var physics = _physics.Active ? "rapier" : $"kinematic ({_physics.Reason})";
        var performance = NetworkPerformance.Instance;

        // Sent against full is what diff states buy: only the properties that changed go out
        var traffic = performance.IsEnabled()
            ? $"  props {performance.GetSentStatePropsCount()}/{performance.GetFullStatePropsCount()}"
            : "";

        _status.Text = $"peer #{Multiplayer.GetUniqueId()}  tick {NetworkTime.Instance.Tick}  " +
                       $"players {_players.Count}  shots {_scoreboard.Shots}  " +
                       $"rollback {rollback.RollbackFrom}>{rollback.Tick}  physics {physics}{traffic}";
    }

    private void Host()
    {
        var peer = new ENetMultiplayerPeer();
        if (Report(peer.CreateServer(Port, 8))) return;
        Multiplayer.MultiplayerPeer = peer;
    }

    private void Join()
    {
        var peer = new ENetMultiplayerPeer();
        var address = _address.Text.Length > 0 ? _address.Text : "127.0.0.1";
        if (Report(peer.CreateClient(address, Port))) return;
        Multiplayer.MultiplayerPeer = peer;
    }

    /// <summary>Shows the error and returns true when there is one.</summary>
    private bool Report(Error error)
    {
        if (error == Error.Ok) return false;
        _status.Text = $"Could not start: {error}";
        return true;
    }

    private void HandleServerStart()
    {
        _lobby.Hide();
        SpawnPlayer(1);
    }

    private void HandleClientStart(int id)
    {
        _lobby.Hide();
        SpawnPlayer(id);
    }

    /// <summary>
    /// One player per peer, under the same name on every peer: netfox addresses nodes by their path, so a player the
    /// host calls "Player_2" has to be "Player_2" everywhere.
    /// </summary>
    private void SpawnPlayer(int peer)
    {
        if (_players.ContainsKey(peer)) return;

        var player = PlayerScene.Instantiate<Node3D>();
        player.Name = $"Player_{peer}";
        player.Position = new Vector3(peer % 4 * 2 - 3, 2, peer / 4 % 4 * 2 - 3);

        // The host simulates and replicates every player; each peer owns only its own input node
        player.SetMultiplayerAuthority(1);
        player.GetNode<PlayerInput>("Input").SetMultiplayerAuthority(peer);

        SpawnRoot.AddChild(player);
        _players[peer] = player;

        // Projectiles live in the world, not under the player, or they would ride along with whoever fired them
        var weapon = player.GetNode<PlayerWeapon>("Weapon");
        weapon.SpawnRoot = ProjectileRoot;

        // The host owns the scoreboard, so only it counts; every peer raises the event for its own accepted shots
        weapon.Fired += _scoreboard.CountShot;

        if (peer == Multiplayer.GetUniqueId())
            GetNode<PlaygroundCamera>("Camera3D").Follow(player);
    }

    private void DespawnPlayer(int peer)
    {
        if (!_players.Remove(peer, out var player)) return;
        player.QueueFree();
    }
}
