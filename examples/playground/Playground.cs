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
            _steam.LobbyReady += lobby =>
            {
                _status.Text = $"Steam lobby {lobby} - share this to be joined";
                // The status line is a Label, so the id is printed too: the console is where it can be copied from
                GD.Print($"Steam lobby id: {lobby}");
            };
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

        // One thing nobody controls. Host-simulated, interpolated on clients, never predicted - see Npc. Built here
        // rather than in the scene so both peers get identical trees from the same line.
        var npcs = new Node3D { Name = "Npcs" };
        GetNode("World").AddChild(npcs);
        npcs.AddChild(new Npc { Name = "Npc_0", Position = new Vector3(3, 1, 1) });

        // NetworkEvents starts and stops the tick loop with the session, so nothing here has to
        NetworkEvents.Instance.OnServerStart += HandleServerStart;
        NetworkEvents.Instance.OnClientStart += HandleClientStart;
        NetworkEvents.Instance.OnPeerJoin += SpawnPlayer;
        NetworkEvents.Instance.OnPeerLeave += DespawnPlayer;

        // NetworkSimulator is an autoload, and it connects the editor's extra instances on its own when autoconnect
        // is on (Project Settings > Netfox > Autoconnect), hosting the latency and loss proxy configured there. It
        // must not also sit in this scene: a second one hosts or joins a second time and overwrites the peer the
        // first one just assigned, which leaves both instances connected to nothing.
        var simulator = GetNodeOrNull<NetworkSimulator>("/root/NetworkSimulator");
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

        // netfox simulates in scene tree order, so every peer has to hold the same order. Join order is not it: a
        // client spawns its own player before it hears about anyone else, so it ends up with the reverse of the
        // host's. Two bodies that touch then resolve the collision in a different sequence on each peer - whoever
        // moves first is blocked by the other's old position - and the two disagree about the result for good.
        SortPlayers();

        // Projectiles live in the world, not under the player, or they would ride along with whoever fired them
        var weapon = player.GetNode<PlayerWeapon>("Weapon");
        weapon.SpawnRoot = ProjectileRoot;

        // The host owns the scoreboard, so only it counts; every peer raises the event for its own accepted shots
        weapon.Fired += _scoreboard.CountShot;

        if (peer == Multiplayer.GetUniqueId())
            GetNode<PlaygroundCamera>("Camera3D").Follow(player);
    }

    /// <summary>By name, because the names are the one thing every peer already agrees on.</summary>
    private void SortPlayers()
    {
        var sorted = SpawnRoot.GetChildren().OrderBy(node => node.Name.ToString(), StringComparer.Ordinal).ToList();
        for (var index = 0; index < sorted.Count; index++) SpawnRoot.MoveChild(sorted[index], index);
    }

    private void DespawnPlayer(int peer)
    {
        if (!_players.Remove(peer, out var player)) return;
        player.QueueFree();
    }
}
