using Godot;
using Netfox;
using Netfox.Extras;

namespace Netfox.Examples.E2E;

/// <summary>Deterministic input: every peer pushes its player along a fixed direction so both sides can be compared.</summary>
public partial class E2EInput : BaseNetInput
{
    [Export] public Vector3 Movement { get; set; }

    protected override void Gather()
    {
        // Peer 1 moves along +X, everyone else along +Z, so the two players are distinguishable in the output
        Movement = GetMultiplayerAuthority() == 1 ? Vector3.Right : Vector3.Back;
    }
}

/// <summary>A player whose position is rollback state driven by its Input child.</summary>
public partial class E2EPlayer : Node3D, IRollbackTick
{
    private const float Speed = 4.0f;

    public E2EInput Input { get; private set; } = null!;
    public int SimulatedTicks { get; private set; }

    public void Setup(int ownerPeer)
    {
        SetMultiplayerAuthority(1);
        Input = new E2EInput { Name = "Input" };
        Input.SetMultiplayerAuthority(ownerPeer);
        AddChild(Input);

        var synchronizer = new RollbackSynchronizer
        {
            Name = "RollbackSynchronizer",
            Root = this,
            StateProperties = [":position"],
            InputProperties = ["Input:Movement"],
            EnablePrediction = false,
        };
        AddChild(synchronizer);

        var interpolator = new TickInterpolator { Name = "TickInterpolator", Root = this, Properties = [":position"] };
        AddChild(interpolator);
    }

    public void RollbackTick(double delta, int tick, bool isFresh)
    {
        Position += Input.Movement * Speed * (float)delta;
        SimulatedTicks++;
    }
}

/// <summary>
/// Two-process end-to-end check over ENet. Run the host, then the client:
///   godot --headless --path . res://examples/e2e/E2E.tscn -- --host --seconds=8
///   godot --headless --path . res://examples/e2e/E2E.tscn -- --join --seconds=6
/// Each process prints an E2E RESULT line and exits 0 on success.
/// </summary>
public partial class E2E : Node
{
    private const int Port = 9977;

    private bool _isHost;
    private double _seconds = 6;
    private int _statesReceived;
    private int _inputsReceived;
    private readonly Dictionary<int, E2EPlayer> _players = new();

    public override async void _Ready()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg == "--host") _isHost = true;
            else if (arg == "--join") _isHost = false;
            else if (arg.StartsWith("--seconds=")) _seconds = double.Parse(arg.Substring("--seconds=".Length), System.Globalization.CultureInfo.InvariantCulture);
        }

        NetworkSynchronizationServer.Instance.OnState += _ => _statesReceived++;
        NetworkSynchronizationServer.Instance.OnInput += _ => _inputsReceived++;

        var peer = new ENetMultiplayerPeer();
        var error = _isHost ? peer.CreateServer(Port, 8) : peer.CreateClient("127.0.0.1", Port);
        if (error != Error.Ok)
        {
            GD.Print($"E2E RESULT role={(_isHost ? "host" : "client")} ok=false reason=peer_{error}");
            GetTree().Quit(1);
            return;
        }
        Multiplayer.MultiplayerPeer = peer;

        if (_isHost)
        {
            SpawnPlayer(1);
            Multiplayer.PeerConnected += id => SpawnPlayer((int)id);
        }
        else
        {
            Multiplayer.ConnectedToServer += () =>
            {
                SpawnPlayer(1);
                SpawnPlayer(Multiplayer.GetUniqueId());
            };
        }

        await ToSignal(GetTree().CreateTimer(_seconds), SceneTreeTimer.SignalName.Timeout);
        Report();
    }

    private void SpawnPlayer(int ownerPeer)
    {
        if (_players.ContainsKey(ownerPeer)) return;
        var player = new E2EPlayer { Name = $"Player_{ownerPeer}" };
        player.Setup(ownerPeer);
        AddChild(player);
        _players[ownerPeer] = player;
    }

    private void Report()
    {
        var time = NetworkTime.Instance;
        var role = _isHost ? "host" : "client";
        var peers = Multiplayer.GetPeers();
        var synced = time.IsInitialSyncDone();

        var ok = synced && time.Tick > 0 && _players.Count >= 2;
        if (_isHost) ok &= _inputsReceived > 0;
        else ok &= _statesReceived > 0;

        var players = string.Join(" ", _players.OrderBy(p => p.Key).Select(p =>
            $"Player_{p.Key}=({p.Value.Position.X:F2},{p.Value.Position.Y:F2},{p.Value.Position.Z:F2})/ticks={p.Value.SimulatedTicks}"));

        GD.Print($"E2E RESULT role={role} ok={ok} peer={Multiplayer.GetUniqueId()} peers=[{string.Join(",", peers)}] tick={time.Tick} synced={synced} " +
                 $"rtt={time.RemoteRtt * 1000:F1}ms states_received={_statesReceived} inputs_received={_inputsReceived} {players}");
        GetTree().Quit(ok ? 0 : 1);
    }
}
