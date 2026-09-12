using System.Globalization;
using Godot;
using Netfox.Extras;
using FileAccess = Godot.FileAccess;

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

    /// <summary>Called with the tick just simulated and the position it produced, for the authoritative trace.</summary>
    public Action<int, Vector3>? OnSimulated;

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
        OnSimulated?.Invoke(tick, Position);
    }
}

/// <summary>
/// Two-process end-to-end check over ENet. Run the host, then the client:
///   godot --headless --path . res://examples/e2e/E2E.tscn -- --host --seconds=14
///   godot --headless --path . res://examples/e2e/E2E.tscn -- --join --seconds=10
/// The host keeps writing the position it simulated for every tick; the client compares the state it received against
/// that trace, so a run checks that replication is exact, not only that packets arrived. The host has to outlive the
/// client, otherwise the client loses its peer and reports on a stopped clock.
/// Add --latency=40 --loss=3 on both sides to route the run through the NetworkSimulator proxy.
/// Each process prints an E2E RESULT line and exits 0 on success.
/// </summary>
public partial class E2E : Node
{
    private const int Port = 9977;
    private const string TracePath = "user://e2e-host-trace.csv";

    private bool _isHost;
    private double _seconds = 6;
    private int _latencyMs;
    private double _lossPercent;
    private int _statesReceived;
    private int _inputsReceived;
    private readonly Dictionary<int, E2EPlayer> _players = new();

    /// <summary>Host: position simulated for each tick. Client: position received for each tick.</summary>
    private readonly Dictionary<int, float> _trace = new();

    public override async void _Ready()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg == "--host") _isHost = true;
            else if (arg == "--join") _isHost = false;
            else if (arg.StartsWith("--seconds=")) _seconds = ParseDouble(arg, "--seconds=");
            else if (arg.StartsWith("--latency=")) _latencyMs = (int)ParseDouble(arg, "--latency=");
            else if (arg.StartsWith("--loss=")) _lossPercent = ParseDouble(arg, "--loss=");
        }

        NetworkSynchronizationServer.Instance.OnState += snapshot =>
        {
            _statesReceived++;
            // Property paths are stored without the leading colon (see PropertyEntry.Parse)
            if (_players.TryGetValue(1, out var player) && snapshot.TryGetProperty(player, "position", out var value))
                _trace[snapshot.Tick] = value.AsVector3().X;
        };
        NetworkSynchronizationServer.Instance.OnInput += _ => _inputsReceived++;

        var simulated = _latencyMs > 0 || _lossPercent > 0;
        var peer = new ENetMultiplayerPeer();
        Error error;

        if (_isHost)
        {
            error = peer.CreateServer(Port, 8);
            if (error == Error.Ok && simulated)
            {
                var simulator = new NetworkSimulator { Name = "NetworkSimulator" };
                AddChild(simulator);
                var proxyPort = simulator.StartProxy(Port, _latencyMs, _lossPercent);
                GD.Print($"E2E proxy on port {proxyPort} with {_latencyMs}ms latency and {_lossPercent}% loss");
            }
        }
        else
        {
            error = peer.CreateClient("127.0.0.1", simulated ? Port + 1 : Port);
        }

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

            // The client reads this while the host is still running, so keep it fresh
            var flush = new Godot.Timer { Name = "TraceFlush", WaitTime = 0.5, Autostart = true };
            flush.Timeout += WriteTrace;
            AddChild(flush);
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
        // Simulating tick T produces the state netfox records and sends as tick T + 1 (network-rollback.gd:428)
        if (_isHost && ownerPeer == 1)
            player.OnSimulated = (tick, position) => _trace[tick + 1] = position.X;
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
        var comparison = "";

        if (_isHost)
        {
            ok &= _inputsReceived > 0;
            WriteTrace();
        }
        else
        {
            ok &= _statesReceived > 0;

            var hostTrace = ReadTrace();
            var compared = 0;
            var maxError = 0.0f;
            foreach (var (tick, x) in _trace)
            {
                if (!hostTrace.TryGetValue(tick, out var hostX)) continue;
                compared++;
                maxError = Math.Max(maxError, Math.Abs(hostX - x));
            }

            // Replicated state is the host's own state, so it has to match exactly, not approximately
            ok &= compared >= 20 && maxError < 1e-4f;
            comparison = FormattableString.Invariant($" compared_ticks={compared} max_error={maxError:G4}");
        }

        var players = string.Join(" ", _players.OrderBy(p => p.Key).Select(p => FormattableString.Invariant(
            $"Player_{p.Key}=({p.Value.Position.X:F2},{p.Value.Position.Y:F2},{p.Value.Position.Z:F2})/ticks={p.Value.SimulatedTicks}")));

        var head = FormattableString.Invariant(
            $"E2E RESULT role={role} ok={ok} peer={Multiplayer.GetUniqueId()} peers=[{string.Join(",", peers)}] tick={time.Tick} synced={synced}");
        var tail = FormattableString.Invariant(
            $"rtt={time.RemoteRtt * 1000:F1}ms states_received={_statesReceived} inputs_received={_inputsReceived}{comparison} {players}");
        GD.Print($"{head} {tail}");
        GetTree().Quit(ok ? 0 : 1);
    }

    private void WriteTrace()
    {
        using var file = FileAccess.Open(TracePath, FileAccess.ModeFlags.Write);
        if (file is null)
        {
            GD.Print($"E2E could not write {TracePath}: {FileAccess.GetOpenError()}");
            return;
        }

        foreach (var (tick, x) in _trace.OrderBy(entry => entry.Key))
            file.StoreLine(FormattableString.Invariant($"{tick},{x:R}"));
    }

    private static Dictionary<int, float> ReadTrace()
    {
        var result = new Dictionary<int, float>();
        using var file = FileAccess.Open(TracePath, FileAccess.ModeFlags.Read);
        if (file is null)
        {
            GD.Print($"E2E could not read {TracePath}: {FileAccess.GetOpenError()} (run the host first, and let it finish before the client)");
            return result;
        }

        while (!file.EofReached())
        {
            // The host may be writing this very file, so a torn last line is expected
            var parts = file.GetLine().Split(',');
            if (parts.Length != 2) continue;
            if (!int.TryParse(parts[0], CultureInfo.InvariantCulture, out var tick)) continue;
            if (!float.TryParse(parts[1], CultureInfo.InvariantCulture, out var x)) continue;
            result[tick] = x;
        }
        return result;
    }

    private static double ParseDouble(string arg, string prefix)
        => double.Parse(arg[prefix.Length..], CultureInfo.InvariantCulture);
}
