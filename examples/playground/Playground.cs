using Godot;
using Netfox.Extras;

namespace Netfox.Examples.Playground;

/// <summary>
/// The co-op sample: host or join, walk around, push and grab crates, push other players, shoot. Everything replicated
/// is a <see cref="NetworkObject"/>; crates are tinted with the colour of the peer that simulates them right now.
/// <para>
/// Run two or more windows (Debug > Customize Run Instances), Host in one and Join in the others. The host puts a
/// network simulator between itself and the others with the profile from Project Settings > Netfox > Autoconnect >
/// Simulated Profile, or <c>-- --profile=bad</c> on the command line.
/// </para>
/// </summary>
public partial class Playground : Node3D
{
    public const int Port = 9999;
    private const int CrateCount = 12;

    /// <summary>One colour per player slot, in joining order: the host is red, the next player blue, and so on.</summary>
    public static readonly Color[] SlotColors =
    [
        new(0.9f, 0.25f, 0.25f), new(0.25f, 0.5f, 0.95f), new(0.3f, 0.8f, 0.35f), new(0.95f, 0.8f, 0.2f),
        new(0.7f, 0.35f, 0.9f), new(0.95f, 0.55f, 0.15f), new(0.2f, 0.85f, 0.85f), new(0.95f, 0.45f, 0.75f),
    ];

    private static readonly Dictionary<int, int> Slots = new();

    /// <summary>Raised when a player's slot becomes known or goes away, so things tinted by peer can repaint.</summary>
    public static event Action? SlotsChanged;

    public static void SetSlot(int peer, int? slot)
    {
        if (slot is { } value) Slots[peer] = value;
        else Slots.Remove(peer);
        SlotsChanged?.Invoke();
    }

    /// <summary>The colour of a peer's player slot; grey until that player has arrived.</summary>
    public static Color ColorOf(int peer) => Slots.TryGetValue(peer, out var slot) ? SlotColors[slot % SlotColors.Length] : Colors.Gray;

    public Node3D Players { get; private set; } = null!;
    public Node3D Shots { get; private set; } = null!;

    private Control _menu = null!;
    private LineEdit _address = null!;
    private Label _status = null!;
    private Camera3D _camera = null!;
    private MultiplayerSpawner _playerSpawner = null!;
    private NetworkSimulator.Profile _profile = NetworkSimulator.Profile.Default;

    public override void _Ready()
    {
        _profile = ReadProfile();
        BuildWorld();
        BuildUi();

        // Editor autoconnect (Project Settings > Netfox > Autoconnect > Enabled): the first instance hosts, the rest
        // join, through the simulator with the profile set there. The peer is assigned after these events fire.
        if (GetNodeOrNull<NetworkSimulator>("/root/NetworkSimulator") is { } simulator)
        {
            simulator.ServerCreated += () => Callable.From(StartHosting).CallDeferred();
            simulator.ClientConnected += () => _menu.Hide();
        }

        if (OS.GetCmdlineUserArgs().Contains("--smoke")) AddChild(new PlaygroundSmoke { Name = "Smoke" });
        if (OS.GetCmdlineUserArgs().Contains("--host")) Host();
        else if (OS.GetCmdlineUserArgs().Contains("--join")) Join("127.0.0.1");
    }

    private static NetworkSimulator.Profile ReadProfile()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--profile=") && NetworkSimulator.Profile.Named(arg["--profile=".Length..]) is { } named)
                return named;
        return NetworkSimulator.Profile.Named(NetfoxSettings.Instance.SimulatedProfile) ?? NetworkSimulator.Profile.Default;
    }

    private bool ThroughSimulator => _profile != NetworkSimulator.Profile.Clear;

    public void Host()
    {
        var peer = new ENetMultiplayerPeer();
        var error = peer.CreateServer(Port, 8);
        if (error != Error.Ok)
        {
            _status.Text = $"Hosting failed: {error}";
            return;
        }

        Multiplayer.MultiplayerPeer = peer;
        if (ThroughSimulator) GetNode<NetworkSimulator>("/root/NetworkSimulator").StartProxy(Port, _profile);
        StartHosting();
    }

    /// <summary>Once this peer is the server, by the Host button or by autoconnect: players for everyone who joins.</summary>
    private void StartHosting()
    {
        Multiplayer.PeerConnected += id => SpawnPlayer((int)id);
        Multiplayer.PeerDisconnected += id => Players.GetNodeOrNull($"Player{id}")?.QueueFree();
        SpawnPlayer(1);
        _menu.Hide();
    }

    public void Join(string address)
    {
        var peer = new ENetMultiplayerPeer();
        // The simulator listens one port up; the host started it with the same project settings
        var error = peer.CreateClient(address, ThroughSimulator ? Port + 1 : Port);
        if (error != Error.Ok)
        {
            _status.Text = $"Joining failed: {error}";
            return;
        }

        Multiplayer.MultiplayerPeer = peer;
        _menu.Hide();
    }

    /// <summary>The lowest free slot, so a player who leaves hands their colour to the next one to join.</summary>
    private void SpawnPlayer(int peer)
    {
        var taken = Players.GetChildren().OfType<PlaygroundPlayer>().Select(player => player.Slot).ToHashSet();
        var slot = Enumerable.Range(0, 64).First(candidate => !taken.Contains(candidate));
        _playerSpawner.Spawn(new Godot.Collections.Array { peer, slot });
    }

    private void BuildWorld()
    {
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-50, 30, 0), ShadowEnabled = true });
        AddChild(new WorldEnvironment { Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.55f, 0.65f, 0.75f), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.5f, 0.5f, 0.55f) } });

        AddStatic("Floor", new Vector3(0, -0.5f, 0), new Vector3(40, 1, 40), new Color(0.4f, 0.42f, 0.4f));
        AddStatic("WallNorth", new Vector3(0, 1, -20), new Vector3(40, 2, 1), new Color(0.3f, 0.3f, 0.35f));
        AddStatic("WallSouth", new Vector3(0, 1, 20), new Vector3(40, 2, 1), new Color(0.3f, 0.3f, 0.35f));
        AddStatic("WallEast", new Vector3(20, 1, 0), new Vector3(1, 2, 40), new Color(0.3f, 0.3f, 0.35f));
        AddStatic("WallWest", new Vector3(-20, 1, 0), new Vector3(1, 2, 40), new Color(0.3f, 0.3f, 0.35f));

        // Scene objects: every peer creates the same crates under the same names, and the host simulates them first
        var crates = new Node3D { Name = "Crates" };
        AddChild(crates);
        for (var i = 0; i < CrateCount; i++)
            crates.AddChild(PlaygroundCrate.Create($"Crate{i}", new Vector3(-6 + i % 4 * 4, 0.5f + i / 4 * 1.01f, -4)));

        Players = new Node3D { Name = "Players" };
        AddChild(Players);
        Shots = new Node3D { Name = "Shots" };
        AddChild(Shots);

        _playerSpawner = new MultiplayerSpawner { Name = "PlayerSpawner", SpawnPath = new NodePath("../Players") };
        _playerSpawner.SpawnFunction = Callable.From((Variant data) => (Node)PlaygroundPlayer.Create(data.AsGodotArray()[0].AsInt32(), data.AsGodotArray()[1].AsInt32()));
        AddChild(_playerSpawner);

        _camera = new Camera3D { Position = new Vector3(0, 14, 16), RotationDegrees = new Vector3(-45, 0, 0) };
        AddChild(_camera);
    }

    private void AddStatic(string name, Vector3 position, Vector3 size, Color color)
    {
        var body = new StaticBody3D { Name = name, Position = position };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = new StandardMaterial3D { AlbedoColor = color } });
        AddChild(body);
    }

    private void BuildUi()
    {
        var layer = new CanvasLayer();
        AddChild(layer);

        _status = new Label { Position = new Vector2(12, 8) };
        layer.AddChild(_status);

        var menu = new VBoxContainer { Position = new Vector2(12, 80), CustomMinimumSize = new Vector2(240, 0) };
        _menu = menu;
        layer.AddChild(menu);

        var host = new Button { Text = "Host" };
        host.Pressed += Host;
        menu.AddChild(host);

        _address = new LineEdit { Text = "127.0.0.1" };
        menu.AddChild(_address);

        var join = new Button { Text = "Join" };
        join.Pressed += () => Join(_address.Text);
        menu.AddChild(join);
    }

    public override void _Process(double delta)
    {
        var time = NetworkTime.Instance;
        var local = Players.GetNodeOrNull<PlaygroundPlayer>($"Player{Multiplayer.GetUniqueId()}");
        var profile = ThroughSimulator ? $"{_profile.LatencyMs}ms each way, {_profile.JitterMs}ms jitter, {_profile.PacketLossPercent}% loss" : "no simulated conditions";

        var delayLines = Players.GetChildren().OfType<PlaygroundPlayer>()
            .Where(player => player.Peer != Multiplayer.GetUniqueId())
            .Select(player => (Player: player, Status: NetworkObjectServer.Instance.GetPlaybackStatus(player.Peer)))
            .Where(entry => entry.Status is not null)
            .Select(entry =>
            {
                var status = entry.Status!.Value;
                var millisecondsPerTick = 1000.0 / time.Tickrate;
                var network = Math.Max(0, time.Tick - status.NewestTick) * millisecondsPerTick;
                var playback = Math.Max(0, status.NewestTick - status.DisplayTick) * millisecondsPerTick;
                return $"Peer {entry.Player.Peer}: {network + playback:F0}ms behind ({network:F0}ms network + {playback:F0}ms playback)";
            });
        var delayReadout = string.Join('\n', delayLines);
        if (delayReadout.Length > 0) delayReadout = "\n" + delayReadout;

        _status.Text = Multiplayer.MultiplayerPeer is null or OfflineMultiplayerPeer
            ? $"Host, or join an address. Network: {profile}"
            : $"Peer {Multiplayer.GetUniqueId()}  tick {time.Tick}  rtt {time.RemoteRtt * 1000:F0}ms  network: {profile}\n" +
              "WASD move, Space jump, F grab / throw, E push a player, left mouse or Enter shoot. Crates show who simulates them." +
              delayReadout;

        if (local is not null)
            _camera.GlobalPosition = _camera.GlobalPosition.Lerp(local.GlobalPosition + new Vector3(0, 11, 11), (float)Math.Min(1, delta * 6));
    }
}
