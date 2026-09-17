using Godot;
using Netfox.Examples.Steam;
using Netfox.Extras;

namespace Netfox.Examples.Playground;

/// <summary>
/// The co-op sample: host or join, walk around, push and grab crates, push other players, shoot. Everything replicated
/// is a <see cref="NetworkObject"/>; crates are tinted with the colour of the peer that simulates them right now.
/// <para>
/// Run two or more windows (Debug > Customize Run Instances), Host in one and Join in the others. The host puts a
/// in-process link simulator on each mesh connection with the profile from Project Settings &gt; Netfox &gt;
/// Autoconnect &gt; Simulated Profile, or <c>-- --profile=bad</c> on the command line.
/// </para>
/// </summary>
public partial class Playground : Node3D
{
    public const int Port = 9999;

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
    private PlaygroundMesh _mesh = null!;
    private readonly Dictionary<PlaygroundCrate, Transform3D> _crateStarts = new();
    private NetworkSimulator.Profile _profile = NetworkSimulator.Profile.Default;
    private double _sinceReadout = 1;
    private string _delayReadout = "";
    private int _port = Port;
    private SteamLobbyBootstrap? _steam;
    /// <summary>The last word from Steam, kept under the status line, which is rewritten every frame.</summary>
    private string _steamNote = "";

    public override void _Ready()
    {
        _profile = ReadProfile();
        _port = ReadPort();
        Players = GetNode<Node3D>("Players");
        Shots = GetNode<Node3D>("Shots");
        _camera = GetNode<Camera3D>("Camera3D");
        _mesh = GetNode<PlaygroundMesh>("Mesh");
        BindUi();
        // Where the scene put each crate, for the Reset button
        foreach (var crate in GetNode("Crates").GetChildren().OfType<PlaygroundCrate>()) _crateStarts[crate] = crate.GlobalTransform;

        _mesh.MeshReady += id =>
        {
            _menu.Hide();
            if (id == 1) StartHosting();
        };

        // Editor autoconnect (Project Settings > Netfox > Autoconnect > Enabled): the first instance hosts, the rest
        // join. The simulator's temporary star elects the role, then the playground replaces it with its ENet mesh.
        if (GetNodeOrNull<NetworkSimulator>("/root/NetworkSimulator") is { } simulator)
        {
            simulator.ServerCreated += () => Callable.From(() =>
            {
                Multiplayer.MultiplayerPeer?.Close();
                Multiplayer.MultiplayerPeer = null;
                StartMeshHost(simulator.Conditions);
            }).CallDeferred();
            simulator.ClientConnected += () => Callable.From(() =>
            {
                Multiplayer.MultiplayerPeer?.Close();
                Multiplayer.MultiplayerPeer = null;
                StartMeshClient(simulator.Hostname, simulator.Conditions);
            }).CallDeferred();
        }

        if (SteamLobbyBootstrap.IsAvailable && !OS.GetCmdlineUserArgs().Contains("--smoke")) StartSteam();

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

    private static int ReadPort()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--port=") && int.TryParse(arg["--port=".Length..], out var port) && port is > 0 and < 65000)
                return port;
        return Port;
    }

    private bool ThroughSimulator => _profile != NetworkSimulator.Profile.Clear;

    public void Host()
    {
        StartMeshHost(_profile);
    }

    private void StartMeshHost(NetworkSimulator.Profile profile)
    {
        var error = _mesh.Host(_port, profile);
        if (error != Error.Ok) _status.Text = $"Hosting failed: {error}";
    }

    /// <summary>Once this peer is the server, by the Host button or by autoconnect: players for everyone who joins.</summary>
    private void StartHosting()
    {
        // A player's character leaves with its peer on its own: nobody else may take it
        Multiplayer.PeerConnected += id => SpawnPlayer((int)id);
        SpawnPlayer(1);
        _menu.Hide();
        GetNode<Button>("Ui/Menu/ResetCrates").Show();
    }

    /// <summary>
    /// Puts every crate back where the scene had it, on the host: it takes each one first, so whoever was simulating
    /// a crate sees it move through the usual state stream rather than by a second authority writing over it.
    /// </summary>
    private void ResetCrates()
    {
        foreach (var (crate, start) in _crateStarts)
        {
            crate.Object.Release();
            crate.Object.Authority.Take();
            crate.GlobalTransform = start;
            crate.LinearVelocity = Vector3.Zero;
            crate.AngularVelocity = Vector3.Zero;
            crate.Sleeping = false;
        }
    }

    public void Join(string address)
    {
        StartMeshClient(address, _profile);
    }

    private void StartMeshClient(string address, NetworkSimulator.Profile profile)
    {
        var error = _mesh.Join(address, _port, profile);
        if (error != Error.Ok) _status.Text = $"Joining failed: {error}";
        else _menu.Hide();
    }

    /// <summary>
    /// Steam, when GodotSteam is installed and the Steam client runs: the host makes a friends-only lobby, a friend
    /// joins it from the Steam overlay (Join game, or an invite) or by pasting the lobby id. Steam relays the traffic,
    /// so no ports and no addresses. The simulated network profile applies on top of Steam's real network.
    /// </summary>
    private void StartSteam()
    {
        _steam = new SteamLobbyBootstrap { Name = "Steam", Conditions = _profile };
        _steam.Failed += reason => _steamNote = $"\nSteam: {reason}";
        _steam.LobbyReady += lobbyId =>
        {
            _menu.Hide();
            if (!Multiplayer.IsServer()) return;
            DisplayServer.ClipboardSet(lobbyId.ToString());
            _steamNote = $"\nSteam lobby {lobbyId} (copied to the clipboard)";
            StartHosting();
            Engine.GetSingleton("Steam").Call("activateGameOverlayInviteDialog", lobbyId);
        };
        AddChild(_steam);
        if (Engine.GetSingleton("Steam") is { } steam)
            steam.Connect("join_requested", Callable.From<ulong, ulong>((lobbyId, _) => _steam.Join(lobbyId)));
    }

    /// <summary>The lowest free slot, so a player who leaves hands their colour to the next one to join.</summary>
    private void SpawnPlayer(int peer)
    {
        var taken = Players.GetChildren().OfType<PlaygroundPlayer>().Select(player => player.Slot).ToHashSet();
        var slot = Enumerable.Range(0, 64).First(candidate => !taken.Contains(candidate));
        PlaygroundPlayer.Spawn(new Transform3D(Basis.Identity, new Vector3(-6 + slot * 2, 1, 6)), slot, parent: Players, authority: peer);
    }

    private void BindUi()
    {
        _status = GetNode<Label>("Ui/Status");
        _menu = GetNode<Control>("Ui/Menu");
        _address = GetNode<LineEdit>("Ui/Menu/Address");
        GetNode<Button>("Ui/Menu/Host").Pressed += Host;
        GetNode<Button>("Ui/Menu/Join").Pressed += () => Join(_address.Text);
        GetNode<Button>("Ui/Menu/ResetCrates").Pressed += ResetCrates;

        if (!SteamLobbyBootstrap.IsAvailable) return;
        var steamHost = GetNode<Button>("Ui/Menu/SteamHost");
        steamHost.Pressed += () => _steam?.Host();
        steamHost.Show();
        var steamJoin = GetNode<Button>("Ui/Menu/SteamJoin");
        steamJoin.Pressed += () =>
        {
            if (ulong.TryParse(_address.Text.Trim(), out var lobbyId)) _steam?.Join(lobbyId);
            else _steamNote = "\nSteam: paste the host's lobby id into the field";
        };
        steamJoin.Show();
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, PhysicalKeycode: Key.F3 }) PlaytestLog.On = !PlaytestLog.On;
    }

    public override void _PhysicsProcess(double delta)
        => PlaytestLog.Frame(this, GetNode("Crates").GetChildren().OfType<PlaygroundCrate>().ToList(),
            Players.GetChildren().OfType<PlaygroundPlayer>());

    public override void _Process(double delta)
    {
        var time = NetworkTime.Instance;
        var local = Players.GetNodeOrNull<PlaygroundPlayer>($"Player{Multiplayer.GetUniqueId()}");
        var profile = ThroughSimulator ? $"{_profile.LatencyMs}ms each way, {_profile.JitterMs}ms jitter, {_profile.PacketLossPercent}% loss" : "no simulated conditions";

        // Once a second: an average that changes every frame cannot be read
        _sinceReadout += delta;
        if (_sinceReadout >= 1)
        {
            _sinceReadout = 0;
            var millisecondsPerTick = 1000.0 / time.Tickrate;
            _delayReadout = string.Concat(Players.GetChildren().OfType<PlaygroundPlayer>()
                .Where(player => player.Peer != Multiplayer.GetUniqueId())
                .Select(player => (Player: player, Status: NetworkObjectServer.Instance.Diagnostics.GetPlaybackStatus(player.Peer)))
                .Where(entry => entry.Status is not null)
                .Select(entry =>
                {
                    var status = entry.Status!.Value;
                    return $"\nPeer {entry.Player.Peer}: {status.TotalTicks * millisecondsPerTick:F0}ms behind " +
                           $"({status.NetworkTicks * millisecondsPerTick:F0}ms network + {status.PlaybackTicks * millisecondsPerTick:F0}ms playback)";
                }));
        }
        var delayReadout = _delayReadout;

        _status.Text = Multiplayer.MultiplayerPeer is null or OfflineMultiplayerPeer
            ? $"Host, or join an address. Network: {profile}"
            : $"Peer {Multiplayer.GetUniqueId()}  tick {time.Tick}  rtt {time.RemoteRtt * 1000:F0}ms  network: {profile}\n" +
              "WASD move, Space jump, F grab / throw, E push a player, left mouse or Enter shoot. Crates show who simulates them.\n" +
              $"F3: playtest log {(PlaytestLog.On ? "on" : "off")} (user://playtest, or Project Settings > Playground)" +
              delayReadout;
        _status.Text += _steamNote;

        if (local is not null)
            _camera.GlobalPosition = _camera.GlobalPosition.Lerp(local.GlobalPosition + new Vector3(0, 11, 11), (float)Math.Min(1, delta * 6));
    }
}
