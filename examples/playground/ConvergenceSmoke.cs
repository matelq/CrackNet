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

    /// <summary>
    /// How many players to wait for before the run starts, so a third process can join the same session. Two peers
    /// is the count at which every player is either yours or the host's, which is the one arrangement where nothing
    /// has to be relayed on behalf of somebody else.
    /// </summary>
    private int _peers = 2;

    /// <summary>Write every tick of belief to user://, for diffing the two peers tick by tick.</summary>
    private bool _dump;
    private int _latencyMs;
    private double _lossPercent;

    /// <summary>
    /// A named set of conditions instead of the constant delay and even loss that --latency and --loss give, which no
    /// real link does either of. Null runs on whatever those two flags said.
    /// </summary>
    private NetworkSimulator.Profile? _profile;

    /// <summary>Each player picks up the crate it was steered to, carries it, throws it. netfox-net#56.</summary>
    private bool _pickup;

    /// <summary>Ticks on which this peer believed somebody was carrying a crate, so a pickup run that never picked up cannot pass as one that did.</summary>
    private int _heldTicks;

    /// <summary>A client ran the NPC's rule. It must not: the NPC is told where it is, and that is all.</summary>
    private bool _clientRanNpcRule;

    /// <summary>The proxy, on the host only, so the report can say what the link actually did rather than what it was asked to do.</summary>
    private NetworkSimulator? _proxy;
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

    /// <summary>First tick after everyone let go of the keys: from here on any disagreement is one that did not heal.</summary>
    private int _quietFrom = -1;

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
            else if (arg.StartsWith("--peers=")) _peers = Math.Max(2, (int)Parse(arg, "--peers="));
            else if (arg == "--profile=realistic") _profile = NetworkSimulator.Profile.Realistic;
            else if (arg == "--profile=hostile") _profile = NetworkSimulator.Profile.Hostile;
            else if (arg == "--pickup") _pickup = true;
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
        var conditions = _profile ?? new NetworkSimulator.Profile(_latencyMs, _lossPercent);
        var simulated = _profile is not null || _latencyMs > 0 || _lossPercent > 0;
        if (simulated && !_isHost) _playground.Port += 1;

        _playground.GetNode<Button>(_isHost ? "UI/Lobby/Panel/Rows/Buttons/Host" : "UI/Lobby/Panel/Rows/Buttons/Join")
            .EmitSignal(BaseButton.SignalName.Pressed);

        if (simulated && _isHost)
        {
            _proxy = GetNode<NetworkSimulator>("/root/NetworkSimulator");
            var port = _proxy.StartProxy(_playground.Port, conditions);
            GD.Print($"CONVERGENCE proxy on {port} with {conditions}");
        }

        NetworkTime.Instance.AfterTickLoop += Record;

        // What actually arrives over the wire for each player, so "the state never got here" can be told apart from
        // "the state got here and was not applied"
        if (_dump)
            NetworkSynchronizationServer.Instance.OnState += snapshot =>
            {
                foreach (var player in Ordered())
                    if (snapshot.TryGetProperty(player, "position", out var value))
                        _received.Add(FormattableString.Invariant(
                            $"{snapshot.Tick},{player.Name},{value.AsVector3().X:F4},{value.AsVector3().Y:F4},{value.AsVector3().Z:F4},recv,0"));
            };

        if (!await WaitFor(() => _players.GetChildCount() >= _peers && NetworkTime.Instance.IsInitialSyncDone(), 20))
        {
            Report(false,
                $"saw {_players.GetChildCount()} of {_peers} synchronized players (peer #{Multiplayer.GetUniqueId()})");
            return;
        }

        // Both peers walk their own player onto the same spot, so the two press into each other there. Steering
        // rather than a fixed direction because where a player spawns depends on its peer id, which is a random
        // number on a client - and placing them by hand is not an option: position is rollback state, so an
        // assignment from outside a tick is overwritten from the history on the next one.
        // The origin, with nothing behind it. Steering onto the crate left the players 1.8m apart with the crate
        // between them; steering to a spot beside it left one player squeezed between the other and the crate - the
        // three-body wedge of netfox-net#51, 60cm deep, on a two-peer run that used to read 0.0000. The crates are
        // still compared here (they sit still and agree exactly). --pickup walks onto the crate instead: the first
        // player to arrive grabs it, which is what clears the way for the second.
        _steerTo = _pickup ? Crates()[1].GlobalPosition with { Y = 0 } : Vector3.Zero;

        // One shot each along the way, so the weapon's request and accept round trip is part of the run and the
        // scoreboard has something to disagree about
        Godot.Input.ParseInputEvent(new InputEventAction { Action = "ui_select", Pressed = true });
        await Wait(0.3);
        Godot.Input.ParseInputEvent(new InputEventAction { Action = "ui_select", Pressed = false });

        if (_pickup)
        {
            // Walk up, grab, carry for a while, throw. Held down rather than tapped: the action is edge-free by
            // construction, since SetActive only fires while nothing is held yet.
            await Wait(4.0);
            Hold("ui_focus_next", true);
            await Wait(3.0);
            Hold("ui_focus_next", false);
            Hold("ui_focus_prev", true);
            await Wait(0.5);
            Hold("ui_focus_prev", false);
            await Wait(2.5);
        }
        else
        {
            await Wait(10.0);
        }
        _steerTo = null;
        Release();
        _quietFrom = NetworkTime.Instance.Tick;

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
            // The host picks the window, because only one of the two can. Wait for the trace that has us in it.
            await WaitFor(() => ReadTrace() is { To: >= 0 } trace
                && trace.Ticks.Values.Any(beliefs => beliefs.ContainsKey(_ownName)), 12);
            var trace = ReadTrace();
            _quietFrom = trace.From;
            _captureTick = trace.To;
            Capture(_captureTick);
        }

        Report(true, "");
    }

    public override void _Process(double delta)
    {
        if (_players is null || _players.GetChildCount() < _peers) return;

        if (_steerTo is null) return;

        // Closest any two of them ever got, not just the first two: with three players the pair that meets is not
        // known in advance, and a run where nobody touched proves nothing about collisions.
        // Only while they are being steered: a player that has just been spawned and not yet been told where it is
        // sits at the origin, and on a client that reads as every pair meeting before the run has begun.
        var ordered = Ordered();
        for (var i = 0; i < ordered.Count; i++)
            for (var j = i + 1; j < ordered.Count; j++)
                _closestApproach = Mathf.Min(_closestApproach, Horizontally(ordered[i], ordered[j]));

        // On the platform the target moves, and getting up there needs the jump: its top is 1.2m above the ground,
        // which is one jump with nothing to spare
        var target = _onPlatform ? _platform.GlobalPosition : _steerTo.Value;
        Steer(target);
        if (_onPlatform) Climb(delta, target);
    }

    /// <summary>
    /// How far apart two players are on the floor, ignoring height. Straight line distance answers the wrong
    /// question: a player that climbs on top of another is about 1.6m away by that measure and in contact by any
    /// other, so a run where that happened read as one where they never met.
    /// </summary>
    private static float Horizontally(PlayerCharacter a, PlayerCharacter b)
        => new Vector2(a.Position.X - b.Position.X, a.Position.Z - b.Position.Z).Length();

    /// <summary>Keeps what this peer believes right now, against the tick it believes it for.</summary>
    private void Record()
    {
        var tick = NetworkTime.Instance.Tick;
        var beliefs = Ordered().ToDictionary(player => player.Name.ToString(), Settle);
        if (beliefs.Values.Any(belief => belief.JumpsLeft >= 0 && IsPlayer(belief)) && _pickup) _heldTicks++;

        // Crates too, by name like the players. They are the one thing here that rolls a real physics space back,
        // and until this line nothing compared what two peers believed about them - a crate in two places on two
        // screens passed. They are host-simulated and never predicted, so the expectation is the tight one.
        foreach (var crate in Crates())
            beliefs[crate.Name.ToString()] = new SettledPlayer(crate.Position, "crate", 0);

        // And the NPC: the one root nobody drives. Host-simulated and never predicted, so the same tight expectation
        foreach (var npc in _playground.GetNode("World/Npcs").GetChildren().OfType<Npc>())
        {
            beliefs[npc.Name.ToString()] = new SettledPlayer(npc.Position, "npc", 0);
            if (!_isHost && npc.SimulatedTicks > 0) _clientRanNpcRule = true;
        }
        _history[tick] = beliefs;
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

    private static bool IsPlayer(SettledPlayer belief) => belief.State is not ("crate" or "npc");

    private List<Node3D> Crates()
        => _playground.GetNode("World/Crates").GetChildren().OfType<Node3D>()
            .OrderBy(crate => crate.Name.ToString(), StringComparer.Ordinal).ToList();

    private static SettledPlayer Settle(PlayerCharacter player)
        => new(player.Position, player.StateMachine.State.ToString(), player.HeldCrate);

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
        foreach (var action in new[] { "ui_right", "ui_left", "ui_down", "ui_up", "ui_accept", "ui_focus_next", "ui_focus_prev" }) Hold(action, false);
    }

    private static void Hold(string action, bool pressed)
    {
        if (pressed == Godot.Input.IsActionPressed(action)) return;
        Godot.Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = pressed });
    }

    private void Report(bool reached, string failure)
    {
        var ok = reached && _final.Count(entry => IsPlayer(entry.Value)) >= _peers && _captureTick >= 0;

        // A client must never have simulated the NPC - it is told where it is, and that is all
        if (_clientRanNpcRule)
        {
            failure += " client-ran-npc-rule";
            ok = false;
        }

        // A pickup run has to have carried something, or it tested the crates being shoved and nothing else
        if (_pickup) ok &= _heldTicks > 10;

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
            var unsettled = 0;
            var worstPerPlayer = new Dictionary<string, float>();
            var mismatched = new HashSet<string>();

            foreach (var (tick, theirs) in host.Ticks)
            {
                if (!_history.TryGetValue(tick, out var mine)) continue;
                compared++;
                var bad = false;

                foreach (var (name, theirBelief) in theirs)
                {
                    if (!mine.TryGetValue(name, out var myBelief)) continue;

                    var gap = myBelief.Position.DistanceTo(theirBelief.Position);
                    worst = Mathf.Max(worst, gap);
                    worstPerPlayer[name] = Mathf.Max(worstPerPlayer.GetValueOrDefault(name), gap);
                    if (gap >= Tolerance) bad = true;

                    if (myBelief.State != theirBelief.State) { mismatched.Add($"{name}:state"); bad = true; }
                    if (myBelief.JumpsLeft != theirBelief.JumpsLeft) { mismatched.Add($"{name}:jumps"); bad = true; }
                }

                if (bad) unsettled++;
            }

            var disagreements = worstPerPlayer
                .Where(entry => entry.Value >= Tolerance)
                .Select(entry => FormattableString.Invariant($"{entry.Key}:pos{entry.Value:F3}"))
                .Concat(mismatched)
                .ToList();
            if (_finalShots != host.Shots) disagreements.Add($"score({_finalShots}!={host.Shots})");

            // Both peers have to have seen roughly the same amount of carrying. A client that predicted a pickup the
            // host never ruled on carried a crate for a hundred ticks nobody confirmed - and the quiet window, which
            // starts after release, cannot see that. Two round trips of slack for the prediction being ahead.
            if (_pickup && Math.Abs(_heldTicks - host.Held) > 20) disagreements.Add($"held({_heldTicks}!={host.Held})");

            // Everything here is replicated from its authority every tick, and by now nobody has pressed anything
            // for seconds. A gap that is still there is a peer that took the authority's state and walked away from
            // it, and one tick of it is one tick too many.
            ok &= compared >= 30 && unsettled == 0 && disagreements.Count == 0;
            comparison = FormattableString.Invariant(
                $" window={compared}ticks unsettled={unsettled} worst_disagreement={worst:F4}/{Tolerance} disagreements=[{string.Join(" ", disagreements)}]");
        }

        var beliefs = string.Join(" ", _final.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry =>
            FormattableString.Invariant(
                $"{entry.Key}=({entry.Value.Position.X:F3},{entry.Value.Position.Y:F3},{entry.Value.Position.Z:F3})/{entry.Value.State}/{entry.Value.JumpsLeft}")));
        if (_dump) Dump();

        var head = FormattableString.Invariant(
            $"CONVERGENCE role={(_isHost ? "host" : "client")} ok={ok} peer=#{Multiplayer.GetUniqueId()} at_tick={_captureTick}");
        var tail = FormattableString.Invariant(
            $"mode={(_onPlatform ? "platform" : "ground")} players={_final.Count(entry => IsPlayer(entry.Value))} crates={_final.Count(entry => entry.Value.State == "crate")} npcs={_final.Count(entry => entry.Value.State == "npc")} held_ticks={_heldTicks} collided={collided} closest={_closestApproach:F3} shots={_finalShots}{comparison} {beliefs} {failure}");
        GD.Print($"{head} {tail}");

        // What the link did, not what it was asked to do. A configured burst that never fires reads exactly like a
        // clean run otherwise, and that is the kind of check that passes for the wrong reason.
        if (_proxy is not null)
        {
            var counts = _proxy.ProxyCounts;
            GD.Print(FormattableString.Invariant(
                $"CONVERGENCE link: forwarded={counts.Forwarded} dropped={counts.Dropped} burst_dropped={counts.BurstDropped}"));
        }

        GetTree().Quit(ok ? 0 : 1);
    }

    private readonly List<string> _received = new();

    /// <summary>Every tick this peer recorded, so the two runs can be diffed to find where they first parted.</summary>
    private void Dump()
    {
        using (var incoming = FileAccess.Open($"user://received-{(_isHost ? "host" : "client")}.csv", FileAccess.ModeFlags.Write))
            if (incoming is not null)
                foreach (var line in _received) incoming.StoreLine(line);

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

    /// <summary>
    /// The host's belief for every tick of the quiet window, not just for one of them.
    /// <para>
    /// A single tick is a coin toss. Whether two capsules happen to be deeply inside each other at the moment you
    /// look swings the answer by a metre, so comparing one tick measures luck rather than the thing under test. The
    /// whole quiet window - after the keys are released, once corrections have had seconds to land - is where an
    /// unhealed disagreement has nowhere left to hide.
    /// </para>
    /// </summary>
    private void WriteTrace()
    {
        using var file = FileAccess.Open(TracePath, FileAccess.ModeFlags.Write);
        if (file is null)
        {
            GD.Print($"CONVERGENCE could not write {TracePath}: {FileAccess.GetOpenError()}");
            return;
        }

        file.StoreLine(FormattableString.Invariant($"#window,{_quietFrom},{_captureTick},{_finalShots},{_heldTicks}"));
        for (var tick = _quietFrom; tick <= _captureTick; tick++)
        {
            if (!_history.TryGetValue(tick, out var beliefs)) continue;
            foreach (var (name, settled) in beliefs)
                file.StoreLine(FormattableString.Invariant($"{tick},{name},{settled.Encode()}"));
        }
    }

    private static (Dictionary<int, Dictionary<string, SettledPlayer>> Ticks, int From, int To, int Shots, int Held) ReadTrace()
    {
        var ticks = new Dictionary<int, Dictionary<string, SettledPlayer>>();
        var from = -1;
        var to = -1;
        var shots = -1;
        var held = -1;

        using var file = FileAccess.Open(TracePath, FileAccess.ModeFlags.Read);
        if (file is null) return (ticks, from, to, shots, held);

        while (!file.EofReached())
        {
            var parts = file.GetLine().Split(',');
            if (parts is ["#window", var f, var t, var s, var h])
            {
                int.TryParse(f, CultureInfo.InvariantCulture, out from);
                int.TryParse(t, CultureInfo.InvariantCulture, out to);
                int.TryParse(s, CultureInfo.InvariantCulture, out shots);
                int.TryParse(h, CultureInfo.InvariantCulture, out held);
                continue;
            }

            if (parts.Length != 7) continue;
            if (!int.TryParse(parts[0], CultureInfo.InvariantCulture, out var tick)) continue;
            if (!SettledPlayer.TryDecode(parts[1..], out var settled)) continue;

            if (!ticks.TryGetValue(tick, out var beliefs)) ticks[tick] = beliefs = new Dictionary<string, SettledPlayer>();
            beliefs[parts[1]] = settled;
        }
        return (ticks, from, to, shots, held);
    }
}
