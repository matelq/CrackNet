using System.Globalization;
using Godot;
using Netfox.Extras;
using FileAccess = Godot.FileAccess;

namespace Netfox.Examples.Playground;

/// <summary>What one peer believed about one player on one tick.</summary>
public readonly record struct SettledPlayer(Vector3 Position, string State, int JumpsLeft)
{
    public string Encode()
        => FormattableString.Invariant($"{Position.X:R},{Position.Y:R},{Position.Z:R},{State},{JumpsLeft}");

    public static bool TryDecode(string[] parts, out SettledPlayer player)
    {
        player = default;
        if (parts.Length != 6) return false;
        if (!float.TryParse(parts[1], CultureInfo.InvariantCulture, out var x)) return false;
        if (!float.TryParse(parts[2], CultureInfo.InvariantCulture, out var y)) return false;
        if (!float.TryParse(parts[3], CultureInfo.InvariantCulture, out var z)) return false;
        if (!int.TryParse(parts[5], CultureInfo.InvariantCulture, out var jumps)) return false;
        player = new SettledPlayer(new Vector3(x, y, z), parts[4], jumps);
        return true;
    }
}

/// <summary>
/// Two processes, two players pushed into each other, and then a question the other checks never ask: on one and the
/// same tick, do both peers agree on where everyone is?
/// <para>
/// <c>examples/e2e</c> compares what a client received against what the host simulated, which proves the wire is
/// exact. It cannot see a client that receives the right state and then simulates its way somewhere else - and a
/// disagreement that outlives the input is precisely the failure that is invisible in one window and obvious the
/// moment two people play. Colliding bodies are where it comes from: resolving a collision needs both bodies at the
/// tick being resolved, and a peer that has to guess the other's input does not have that.
/// </para>
/// <para>
/// Both peers keep a per-tick record of what they believe. The host picks a tick once everything has settled and
/// writes its own record for it; the client looks up the same tick in its own. Comparing by tick rather than by wall
/// clock is what makes <c>--on-platform</c> meaningful: the platform never stops, so two peers reading their own
/// clocks would disagree about a rider for no better reason than having looked at different moments.
/// </para>
/// <para>
/// Run: <c>godot --headless --path . res://examples/playground/ConvergenceSmoke.tscn -- --host</c> and, a moment
/// later, the same with <c>--join</c>. The host outlives the client on purpose. Add <c>--latency=120 --loss=10</c>
/// to both, which is where this stops being a formality, and <c>--on-platform</c> for issue #35.
/// </para>
/// </summary>
public partial class ConvergenceSmoke : Node
{
    private const string TracePath = "user://playground-convergence-trace.csv";

    /// <summary>How far apart two peers may put the same player on the same tick.</summary>
    private const float Tolerance = 0.05f;

    /// <summary>Ticks of belief to keep, which only has to outlast the gap between the two reports.</summary>
    private const int HistoryTicks = 1200;

    private bool _isHost;
    private bool _onPlatform;

    /// <summary>Write every tick of belief to user://, for diffing the two peers tick by tick.</summary>
    private bool _dump;
    private int _latencyMs;
    private double _lossPercent;
    private Playground _playground = null!;
    private Node _players = null!;
    private MovingPlatform _platform = null!;

    /// <summary>Closest the two players ever got, so a run that never actually collided can say so.</summary>
    private float _closestApproach = float.PositiveInfinity;

    /// <summary>Where this peer is steering its own player, or null once it has let go.</summary>
    private Vector3? _steerTo;

    private double _jumpPhase;

    /// <summary>What this peer believed on each tick, which is what the two are compared on.</summary>
    private readonly Dictionary<int, Dictionary<string, SettledPlayer>> _history = new();
    private readonly Dictionary<int, int> _shotsAt = new();

    private int _captureTick = -1;
    private Dictionary<string, SettledPlayer> _final = new();
    private int _finalShots;
    private string _ownName = "";

    public override async void _Ready()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg == "--host") _isHost = true;
            else if (arg == "--join") _isHost = false;
            else if (arg == "--on-platform") _onPlatform = true;
            else if (arg == "--dump") _dump = true;
            else if (arg.StartsWith("--latency=")) _latencyMs = (int)Parse(arg, "--latency=");
            else if (arg.StartsWith("--loss=")) _lossPercent = Parse(arg, "--loss=");
        }

        // A trace left by an earlier run has a different random peer id in it, and a client that reads one compares
        // against a stranger. Clearing it here and waiting for our own name below makes that impossible rather than
        // unlikely.
        if (_isHost) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(TracePath));

        _playground = GD.Load<PackedScene>("res://examples/playground/playground.tscn").Instantiate<Playground>();
        AddChild(_playground);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        _players = _playground.GetNode("World/Players");
        _platform = _playground.GetNode<MovingPlatform>("World/Platform");

        // With a proxy in the way the host still listens on its own port and the client goes to the proxy's, which
        // is where the delay and the dropped packets live
        var simulated = _latencyMs > 0 || _lossPercent > 0;
        if (simulated && !_isHost) _playground.Port += 1;

        _playground.GetNode<Button>(_isHost ? "UI/Lobby/Panel/Rows/Buttons/Host" : "UI/Lobby/Panel/Rows/Buttons/Join")
            .EmitSignal(BaseButton.SignalName.Pressed);

        if (simulated && _isHost)
        {
            var port = GetNode<NetworkSimulator>("/root/NetworkSimulator")
                .StartProxy(_playground.Port, _latencyMs, _lossPercent);
            GD.Print($"CONVERGENCE proxy on {port} with {_latencyMs}ms latency and {_lossPercent}% loss");
        }

        NetworkTime.Instance.AfterTickLoop += Record;

        if (!await WaitFor(() => _players.GetChildCount() >= 2 && NetworkTime.Instance.IsInitialSyncDone(), 15))
        {
            Report(false, $"never saw two synchronized players (peer #{Multiplayer.GetUniqueId()})");
            return;
        }

        // Both peers walk their own player onto the same spot, so the two press into each other there. Steering
        // rather than a fixed direction because where a player spawns depends on its peer id, which is a random
        // number on a client - and placing them by hand is not an option: position is rollback state, so an
        // assignment from outside a tick is overwritten from the history on the next one.
        _steerTo = Vector3.Zero;

        // One shot each along the way, so the weapon's request and accept round trip is part of the run and the
        // scoreboard has something to disagree about
        Godot.Input.ParseInputEvent(new InputEventAction { Action = "ui_select", Pressed = true });
        await Wait(0.3);
        Godot.Input.ParseInputEvent(new InputEventAction { Action = "ui_select", Pressed = false });

        await Wait(10.0);
        _steerTo = null;
        Release();

        // Long enough for any correction still in flight to land
        await Wait(6.0);

        _ownName = $"Player_{Multiplayer.GetUniqueId()}";

        if (_isHost)
        {
            _captureTick = NetworkTime.Instance.Tick;
            Capture(_captureTick);

            // The client reads this while the host is still running, so keep it fresh and then outstay the client
            // rather than pulling the peer out from under it
            WriteTrace();
            var flush = new Godot.Timer { Name = "TraceFlush", WaitTime = 0.5, Autostart = true };
            flush.Timeout += WriteTrace;
            AddChild(flush);
            await Wait(8.0);
        }
        else
        {
            // The host picks the tick, because only one of the two can. Wait for the trace that has us in it.
            await WaitFor(() => ReadTrace() is { Tick: >= 0 } trace && trace.Players.ContainsKey(_ownName), 12);
            _captureTick = ReadTrace().Tick;
            Capture(_captureTick);
        }

        Report(true, "");
    }

    public override void _Process(double delta)
    {
        if (_players is null || _players.GetChildCount() < 2) return;

        var ordered = Ordered();
        _closestApproach = Mathf.Min(_closestApproach, ordered[0].Position.DistanceTo(ordered[1].Position));

        if (_steerTo is null) return;

        // On the platform the target moves, and getting up there needs the jump: its top is 1.2m above the ground,
        // which is one jump with nothing to spare
        var target = _onPlatform ? _platform.GlobalPosition : _steerTo.Value;
        Steer(target);
        if (_onPlatform) Climb(delta, target);
    }

    /// <summary>Keeps what this peer believes right now, against the tick it believes it for.</summary>
    private void Record()
    {
        var tick = NetworkTime.Instance.Tick;
        _history[tick] = Ordered().ToDictionary(player => player.Name.ToString(), Settle);
        _shotsAt[tick] = _playground.GetNode<Scoreboard>("World/Scoreboard").Shots;

        var expired = tick - HistoryTicks;
        _history.Remove(expired);
        _shotsAt.Remove(expired);
    }

    /// <summary>Takes this peer's belief for the agreed tick, or the nearest one it has.</summary>
    private void Capture(int tick)
    {
        if (tick < 0) return;
        for (var offset = 0; offset <= 4; offset++)
        {
            if (!_history.TryGetValue(tick - offset, out var beliefs)) continue;
            _final = beliefs;
            _finalShots = _shotsAt.GetValueOrDefault(tick - offset);
            _captureTick = tick - offset;
            return;
        }
    }

    private static SettledPlayer Settle(PlayerCharacter player)
        => new(player.Position, player.StateMachine.State.ToString(), player.JumpsLeft);

    /// <summary>
    /// Players in the same order on every peer. Node names carry the peer id and every peer knows them all, so
    /// sorting by name is enough to have both sides talking about the same player.
    /// </summary>
    private List<PlayerCharacter> Ordered()
        => _players.GetChildren().OfType<PlayerCharacter>()
            .OrderBy(player => player.Name.ToString(), StringComparer.Ordinal).ToList();

    /// <summary>
    /// Holds whatever directions take this peer's own player towards a point, the way someone leaning on the arrow
    /// keys would. The deadzone keeps it from buzzing between two opposite keys once it has arrived.
    /// </summary>
    private void Steer(Vector3 target)
    {
        var self = Ordered().FirstOrDefault(player => player.IsLocal);
        if (self is null) return;

        var offset = target - self.Position;
        Hold("ui_right", offset.X > 0.1f);
        Hold("ui_left", offset.X < -0.1f);
        Hold("ui_down", offset.Z > 0.1f);
        Hold("ui_up", offset.Z < -0.1f);
    }

    /// <summary>Taps jump while still below the platform and close enough to land on it.</summary>
    private void Climb(double delta, Vector3 target)
    {
        var self = Ordered().FirstOrDefault(player => player.IsLocal);
        if (self is null) return;

        var onTop = self.Position.Y > 1.4f;
        var near = new Vector2(self.Position.X - target.X, self.Position.Z - target.Z).Length() < 2.6f;
        if (onTop || !near)
        {
            Hold("ui_accept", false);
            return;
        }

        // Jump is edge detected, so it has to be let go of and pressed again rather than held down
        _jumpPhase += delta;
        if (_jumpPhase < 0.4) return;
        _jumpPhase = 0;
        Hold("ui_accept", !Godot.Input.IsActionPressed("ui_accept"));
    }

    private static void Release()
    {
        foreach (var action in new[] { "ui_right", "ui_left", "ui_down", "ui_up", "ui_accept" }) Hold(action, false);
    }

    private static void Hold(string action, bool pressed)
    {
        if (pressed == Godot.Input.IsActionPressed(action)) return;
        Godot.Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = pressed });
    }

    private void Report(bool reached, string failure)
    {
        var ok = reached && _final.Count >= 2 && _captureTick >= 0;

        // The players have to have actually met, or the run proves nothing about collisions. Two capsules of radius
        // 0.4 resting against each other are 0.8 apart.
        var collided = _closestApproach < 1.2f;
        ok &= collided;

        var comparison = "";
        if (_isHost)
        {
            WriteTrace();
        }
        else
        {
            var host = ReadTrace();
            var worst = 0.0f;
            var compared = 0;
            var disagreements = new List<string>();

            foreach (var (name, mine) in _final)
            {
                if (!host.Players.TryGetValue(name, out var theirs)) continue;
                compared++;

                var gap = mine.Position.DistanceTo(theirs.Position);
                worst = Mathf.Max(worst, gap);
                if (gap >= Tolerance) disagreements.Add(FormattableString.Invariant($"{name}:pos{gap:F3}"));
                if (mine.State != theirs.State) disagreements.Add($"{name}:state({mine.State}!={theirs.State})");
                if (mine.JumpsLeft != theirs.JumpsLeft) disagreements.Add($"{name}:jumps({mine.JumpsLeft}!={theirs.JumpsLeft})");
            }

            if (_finalShots != host.Shots) disagreements.Add($"score({_finalShots}!={host.Shots})");
            if (compared < 2) disagreements.Add($"only-compared-{compared}");

            // The same tick on both sides, and everything here is replicated from its authority every tick, so
            // agreement is not approximate. A gap is a client that took the host's state and walked away from it.
            ok &= compared >= 2 && disagreements.Count == 0;
            comparison = FormattableString.Invariant(
                $" compared={compared} worst_disagreement={worst:F4}/{Tolerance} disagreements=[{string.Join(" ", disagreements)}]");
        }

        var beliefs = string.Join(" ", _final.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry =>
            FormattableString.Invariant(
                $"{entry.Key}=({entry.Value.Position.X:F3},{entry.Value.Position.Y:F3},{entry.Value.Position.Z:F3})/{entry.Value.State}/{entry.Value.JumpsLeft}")));
        if (_dump) Dump();

        var head = FormattableString.Invariant(
            $"CONVERGENCE role={(_isHost ? "host" : "client")} ok={ok} peer=#{Multiplayer.GetUniqueId()} at_tick={_captureTick}");
        var tail = FormattableString.Invariant(
            $"mode={(_onPlatform ? "platform" : "ground")} players={_final.Count} collided={collided} closest={_closestApproach:F3} shots={_finalShots}{comparison} {beliefs} {failure}");
        GD.Print($"{head} {tail}");

        GetTree().Quit(ok ? 0 : 1);
    }

    /// <summary>Every tick this peer recorded, so the two runs can be diffed to find where they first parted.</summary>
    private void Dump()
    {
        var path = $"user://convergence-{(_isHost ? "host" : "client")}.csv";
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (file is null) return;

        foreach (var tick in _history.Keys.OrderBy(key => key))
            foreach (var (name, belief) in _history[tick])
                file.StoreLine(FormattableString.Invariant(
                    $"{tick},{name},{belief.Position.X:F4},{belief.Position.Y:F4},{belief.Position.Z:F4},{belief.State},{belief.JumpsLeft}"));

        GD.Print($"CONVERGENCE dump {ProjectSettings.GlobalizePath(path)} ({_history.Count} ticks)");
    }

    private static double Parse(string arg, string prefix)
        => double.Parse(arg[prefix.Length..], CultureInfo.InvariantCulture);

    private async Task Wait(double seconds)
        => await ToSignal(GetTree().CreateTimer(seconds), Godot.Timer.SignalName.Timeout);

    private async Task<bool> WaitFor(Func<bool> condition, double timeoutSeconds)
    {
        for (var waited = 0.0; waited < timeoutSeconds; waited += 0.25)
        {
            if (condition()) return true;
            await Wait(0.25);
        }
        return condition();
    }

    private void WriteTrace()
    {
        using var file = FileAccess.Open(TracePath, FileAccess.ModeFlags.Write);
        if (file is null)
        {
            GD.Print($"CONVERGENCE could not write {TracePath}: {FileAccess.GetOpenError()}");
            return;
        }

        file.StoreLine(FormattableString.Invariant($"#tick,{_captureTick},{_finalShots}"));
        foreach (var (name, settled) in _final)
            file.StoreLine($"{name},{settled.Encode()}");
    }

    private static (Dictionary<string, SettledPlayer> Players, int Tick, int Shots) ReadTrace()
    {
        var players = new Dictionary<string, SettledPlayer>();
        var tick = -1;
        var shots = -1;

        using var file = FileAccess.Open(TracePath, FileAccess.ModeFlags.Read);
        if (file is null) return (players, tick, shots);

        while (!file.EofReached())
        {
            var parts = file.GetLine().Split(',');
            if (parts is ["#tick", var t, var s])
            {
                int.TryParse(t, CultureInfo.InvariantCulture, out tick);
                int.TryParse(s, CultureInfo.InvariantCulture, out shots);
                continue;
            }
            if (SettledPlayer.TryDecode(parts, out var settled)) players[parts[0]] = settled;
        }
        return (players, tick, shots);
    }
}
