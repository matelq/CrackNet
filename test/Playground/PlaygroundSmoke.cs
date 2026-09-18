using System.Globalization;
using Godot;
using FileAccess = Godot.FileAccess;

namespace CrackNet.Examples.Playground;

/// <summary>
/// Drives the playground headless and checks it. Run a host, client A, then client B; the host must outlive both:
/// --seconds is an upper bound: each role finishes as soon as what it checks has settled, and fails if that never
/// happens in time. Runs on different ports (300 apart) can go in parallel.
///   godot --headless --path . res://examples/playground/playground.tscn -- --smoke --host --seconds=36 --port=20000
///   godot --headless --path . res://examples/playground/playground.tscn -- --smoke --join --smoke-client=a --seconds=22 --port=20000
///   godot --headless --path . res://examples/playground/playground.tscn -- --smoke --join --smoke-client=b --seconds=18 --port=20000
/// All use the same network profile.
/// <para>
/// The client's player walks into a crate, which takes the crate over, then grabs it, carries it and throws it. The
/// client writes down every state it sent for the crate; the host writes down what it displayed for the crate each
/// frame and at which playback tick. At the end the host compares the two during the motion, not at rest: a crate
/// drawn where its simulating peer never had it is the failure this exists for. Both check that the crate went back
/// to the host once it settled. Each process prints a PLAYGROUND SMOKE line and exits 0 on success.
/// </para>
/// <para>
/// It lives in <c>test/</c>, not next to the sample: the playground is there to be read as a game. The playground adds
/// it when started with <c>--smoke</c>, and it drives the guest's player through <c>PlaygroundPlayer.Bot</c>.
/// </para>
/// </summary>
public partial class PlaygroundSmoke : Node
{
    /// <summary>Per port, so smoke runs in parallel do not read each other's trace.</summary>
    private string TracePath => $"user://playground-smoke-client-{_port}.csv";
    private int _port = Playground.Port;
    private const string TraceEnd = "END";

    /// <summary>How long every crate has to stay with the host before a role counts the world as settled.</summary>
    private const double SettledSeconds = 1.0;
    private double _allWithHostFor;
    private bool _sawHost;
    private bool _sawDriver;
    private readonly HashSet<string> _fallen = new();
    private const string CrateName = "Crate0";
    private const double MaxDisplayError = 0.25;
    /// <summary>A carried crate is placed on the hand after the animation: anything beyond float noise is a lag.</summary>
    private const double MaxHandError = 0.005;

    private bool _isHost;
    private bool _isObserver;
    private double _seconds = 12;
    private double _elapsed;
    private double _botClock = -1;

    private Playground _playground = null!;
    private PlaygroundCrate _crate = null!;
    private Vector3 _crateStart;
    private double _crateMaxTravel;
    private PlaygroundCrate _target = null!;
    private PlaygroundCrate _onTop = null!;
    private bool _watchingShot;
    private int _stackHangingFrames;
    private const int MaxStackHangingFrames = 6;
    private bool _shotTookCrate;
    private bool _returnedWhileGuestConnected;

    /// <summary>The host presses Reset crates once it is done checking, and every crate has to land back in place.</summary>
    private double _sinceReset = -1;

    private bool _cratesReset;
    private int _homeAfterReset = -1;
    private readonly Dictionary<StringName, Vector3> _crateHome = new();

    /// <summary>The arena is 40 by 40: a crate farther than this from its start has been blown out of the world.</summary>
    private const double MaxCrateTravel = 45;
    private int _clientPeer;
    private bool _sawGuestCrate;

    /// <summary>While the crate is in a hand: frames measured at the end of the frame, and how far the drawn crate got from the drawn hand.</summary>
    private int _carriedFrames;
    private double _handError;

    private readonly List<(int Tick, Vector3 Position, bool Attached)> _sent = new();
    private readonly List<(double Tick, Vector3 Position)> _displayed = new();
    private readonly HashSet<int> _received = new();

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        _isHost = args.Contains("--host");
        _isObserver = args.Contains("--smoke-client=b");
        foreach (var arg in args)
        {
            if (arg.StartsWith("--seconds=")) _seconds = double.Parse(arg["--seconds=".Length..], CultureInfo.InvariantCulture);
            if (arg.StartsWith("--port=")) _port = int.Parse(arg["--port=".Length..], CultureInfo.InvariantCulture);
        }

        // Measured after every node processed and every deferred call ran, which is when the crate is in the hand
        ProcessPriority = int.MaxValue;
        _playground = GetParent<Playground>();
        _crate = _playground.GetNode<PlaygroundCrate>($"Crates/{CrateName}");
        _crateStart = _crate.GlobalPosition;
        _target = _playground.GetNode<PlaygroundCrate>("Crates/Crate1");
        PlaygroundPlayer.Bot = !_isHost && !_isObserver ? Drive : _ => default;
        // A trace left by an earlier run would be compared against this one's ticks
        if (!_isHost && FileAccess.FileExists(TracePath)) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(TracePath));
        _crate.Net().Diagnostics.SampleSent += RecordSent;
        _onTop = _playground.GetNode<PlaygroundCrate>("Crates/Crate4");
        // Handing everything back when a guest leaves also returns the crate to the host, so only a return while the
        // thrower is still here proves the crate came back because it came to rest
        _crate.Net().AuthorityChanged += () =>
        {
            if (_isHost && _crate.Authority.Peer == 1 && _clientPeer != 0 && Multiplayer.GetPeers().Contains(_clientPeer))
                _returnedWhileGuestConnected = true;
        };
        _crate.Net().Diagnostics.SampleReceived += tick => _received.Add(tick);
    }

    /// <summary>
    /// Faces the other crate and watches for a crate taken with a shot as the cause: a hit takes the crate it lands on.
    /// Only the cause counts - turning to aim can nudge a neighbouring crate and take it too.
    /// </summary>
    private (Vector3, bool, bool, bool) Aim(PlaygroundPlayer me)
    {
        if (!_watchingShot)
        {
            _watchingShot = true;
            // A crate still under this peer's authority is taken without any change to see, and one behind another is
            // never reached: pick a host crate at chest height with nothing else on the line
            var crates = _playground.GetNode("Crates").GetChildren().OfType<PlaygroundCrate>().ToList();
            foreach (var fell in crates.Where(crate => crate.GlobalPosition.Y < -0.5f && !_fallen.Contains(crate.Name)))
            {
                _fallen.Add(fell.Name);
                GD.Print($"CRATE FELL {fell.Name} at {fell.GlobalPosition} v {fell.LinearVelocity} authority {fell.Authority.Peer} holder {fell.ClaimedBy} frozen {fell.Freeze} bot {_botClock:F2}");
                foreach (var other in crates)
                    GD.Print($"  {other.Name} at {other.GlobalPosition} authority {other.Authority.Peer} frozen {other.Freeze}");
                foreach (var player in _playground.Players.GetChildren().OfType<PlaygroundPlayer>())
                    GD.Print($"  {player.Name} at {player.GlobalPosition}");
            }
            _target = crates
                // Out of reach too: a crate the bot bumps while turning to aim is taken by the bump, not by the shot
                .Where(crate => crate.Authority.Peer == 1 && crate.GlobalPosition.Y < 1.2f
                                && crate.GlobalPosition.DistanceTo(me.GlobalPosition) > 2.5f)
                .Where(crate => crates.All(other => other == crate || !Blocks(me.GlobalPosition, crate.GlobalPosition, other.GlobalPosition)))
                .OrderBy(crate => crate.GlobalPosition.DistanceTo(me.GlobalPosition))
                .FirstOrDefault() ?? _target;
            PlaygroundShot.Diagnose = true;
            GD.Print($"SHOT AIM at {_target.Name} (authority {_target.Authority.Peer}) at {_target.GlobalPosition} from {me.GlobalPosition}");
            foreach (var crate in crates)
                crate.Net().AuthorityChanged += () =>
                    _shotTookCrate |= crate.Authority.IsLocal && crate.Net().SpreadCause.Contains("/Shots/");
        }
        return (FlatTo(_target, me) * 0.05f, false, false, false);
    }

    private double _aimAt = -1;

    /// <summary>
    /// Aims and shoots once the thrown crate has come to rest and gone back to the host: still rolling, it can roll
    /// into the line of fire after the target was picked and take the shot.
    /// </summary>
    private (Vector3, bool, bool, bool) Shooting(PlaygroundPlayer me, double t)
    {
        if (_aimAt < 0)
        {
            if (_crate.Authority.Peer != 1) return default;
            _aimAt = t;
        }
        // Step back from the row of crates first: aiming from among them, the bot bumped the thrown crate into the
        // line of fire and the shot hit that one instead
        return (t - _aimAt) switch
        {
            < 1.2 => (Vector3.Back, false, false, false),
            < 1.5 => default,
            < 1.6 => Aim(me),
            < 1.7 => (Vector3.Zero, false, false, true),
            _ => default,
        };
    }

    /// <summary>Whether <paramref name="obstacle"/> sits within reach of the flat line from a shooter to a target.</summary>
    private static bool Blocks(Vector3 from, Vector3 to, Vector3 obstacle)
    {
        var a = new Vector2(from.X, from.Z);
        var b = new Vector2(to.X, to.Z);
        var c = new Vector2(obstacle.X, obstacle.Z);
        var along = (c - a).Dot((b - a).Normalized());
        if (along <= 0 || along >= a.DistanceTo(b)) return false;
        return (a + (b - a).Normalized() * along).DistanceTo(c) < 1.0f && obstacle.Y < 1.5f;
    }

    private static Vector3 FlatTo(Node3D target, Node3D from)
    {
        var offset = target.GlobalPosition - from.GlobalPosition;
        offset.Y = 0;
        return offset.Normalized();
    }

    private (Vector3 Move, bool Grab, bool Push, bool Shoot) Drive(PlaygroundPlayer me)
    {
        if (_botClock < 0) _botClock = 0;
        var t = _botClock;
        var toCrate = _crate.GlobalPosition - me.GlobalPosition;
        toCrate.Y = 0;

        return t switch
        {
            < 1 => default,
            < 4.5 => (toCrate.Normalized(), false, false, false),
            < 4.7 => (Vector3.Zero, true, false, false),
            < 5 => default,
            < 7 => (Vector3.Right, false, false, false),
            < 7.2 => (Vector3.Right, true, false, false),
            // After the throw has settled: face another crate and shoot it
            < 13 => default,
            _ => Shooting(me, t),
        };
    }

    /// <summary>On the client, what it sent for the crate while simulating it: exactly the samples that went out.</summary>
    private void RecordSent(int stateTick)
    {
        if (!_isHost) _sent.Add((stateTick, _crate.GlobalPosition, _crate.AttachedTo is not null));
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_botClock >= 0) _botClock += delta;
    }

    /// <summary>What is drawn this frame: the carried crate against the hand of whoever carries it, on this peer's copies.</summary>
    private void MeasureDrawn()
    {
        if (!IsInsideTree() || _crate.AttachedTo is not PlaygroundPlayer carrier) return;
        _carriedFrames++;
        _handError = Math.Max(_handError, _crate.GlobalPosition.DistanceTo(carrier.GetNode<Node3D>("Hand").GlobalPosition));
    }

    public override void _Process(double delta)
    {
        Callable.From(MeasureDrawn).CallDeferred();
        _elapsed += delta;
        _crateMaxTravel = Math.Max(_crateMaxTravel, _crate.GlobalPosition.DistanceTo(_crateStart));

        // Crate4 stood on Crate0. Once Crate0 has been taken from under it, a Crate4 still up there and frozen here is
        // hanging over nothing, waiting for the host's word that it fell
        var underneathGone = new Vector2(_crate.GlobalPosition.X - _onTop.GlobalPosition.X, _crate.GlobalPosition.Z - _onTop.GlobalPosition.Z).Length() > 1.0f;
        if (!_isHost && !_isObserver && underneathGone && _onTop.GlobalPosition.Y > 1.2f && _onTop.Freeze && !_onTop.Authority.IsLocal)
        {
            if (_stackHangingFrames++ % 60 == 0)
                GD.Print($"STACK HANGING frame {_stackHangingFrames}: Crate4 at {_onTop.GlobalPosition} authority {_onTop.Authority.Peer} " +
                         $"holder {_onTop.ClaimedBy} frozen {_onTop.Freeze}; Crate0 at {_crate.GlobalPosition} authority {_crate.Authority.Peer} holder {_crate.ClaimedBy}");
        }

        if (_isHost && !_crate.Authority.IsLocal)
        {
            _clientPeer = _crate.Authority.Peer;
            if (_crate.Net().Diagnostics.DisplayTick is { } shown && _crate.Visible)
                _displayed.Add((shown, _crate.GlobalPosition));
        }
        if (_isObserver && _crate.Authority.Peer is not 1 && !_crate.Authority.IsLocal && _crate.Visible)
            _sawGuestCrate = true;

        var crates = _playground.GetNode("Crates").GetChildren().OfType<PlaygroundCrate>().ToList();
        foreach (var fell in crates.Where(crate => crate.GlobalPosition.Y < -0.5f && !_fallen.Contains(crate.Name)))
        {
            _fallen.Add(fell.Name);
            GD.Print($"CRATE FELL {fell.Name} at {fell.GlobalPosition} v {fell.LinearVelocity} authority {fell.Authority.Peer} holder {fell.ClaimedBy} frozen {fell.Freeze} bot {_botClock:F2}");
            foreach (var other in crates)
                GD.Print($"  {other.Name} at {other.GlobalPosition} authority {other.Authority.Peer} frozen {other.Freeze}");
            foreach (var player in _playground.Players.GetChildren().OfType<PlaygroundPlayer>())
                GD.Print($"  {player.Name} at {player.GlobalPosition}");
        }
        if (_crateHome.Count == 0) foreach (var crate in crates) _crateHome[crate.Name] = crate.GlobalPosition;
        // A second after the reset, before the bot scatters them again: every crate has to be back where it started
        if (_cratesReset && _sinceReset > 1 && _homeAfterReset < 0)
            _homeAfterReset = crates.Count(crate => _crateHome.TryGetValue(crate.Name, out var home) && crate.GlobalPosition.DistanceTo(home) < 0.1f);
        if (_sinceReset >= 0) _sinceReset += delta;
        _allWithHostFor = crates.All(crate => crate.Authority.Peer == 1) ? _allWithHostFor + delta : 0;
        // Seen while everyone is still here: evaluated at the end, a peer that already left would read as never seen
        _sawHost |= _playground.Players.GetNodeOrNull<PlaygroundPlayer>("Player1") is { Visible: true };
        _sawDriver |= _playground.Players.GetChildren().OfType<PlaygroundPlayer>()
            .Any(player => player.Peer != 1 && player.Peer != Multiplayer.GetUniqueId() && player.Visible);

        if (_elapsed >= _seconds || Settled()) Finish();
    }

    /// <summary>Whether everything this role checks has happened and the world has come to rest since.</summary>
    private bool Settled()
    {
        var restingWithHost = _allWithHostFor >= SettledSeconds;
        if (_isHost)
        {
            // Reset while the guest is still here to watch it: it checks that a reset is drawn as a jump, not a flight
            if (!_cratesReset && _clientPeer != 0 && _returnedWhileGuestConnected)
            {
                _cratesReset = true;
                _sinceReset = 0;
                _playground.ResetCrates();   // the button a player presses, checked here rather than only by hand
            }
            return _cratesReset && _sinceReset > 1 && _returnedWhileGuestConnected && TraceComplete();
        }
        if (_isObserver) return _sawDriver && _sawGuestCrate && _received.Count > 20 && restingWithHost;
        return _aimAt >= 0 && _botClock > _aimAt + 2 && _playground.Shots.GetChildCount() == 0 && restingWithHost;
    }

    private bool TraceComplete()
    {
        if (!FileAccess.FileExists(TracePath)) return false;
        using var file = FileAccess.Open(TracePath, FileAccess.ModeFlags.Read);
        var last = "";
        while (!file.EofReached())
            if (file.GetLine() is { Length: > 0 } line) last = line;
        return last == TraceEnd;
    }

    private void Finish()
    {
        SetProcess(false);
        var backToHost = _crate.Authority.Peer == 1;
        bool ok;
        string detail;

        if (_isHost)
        {
            var sent = ReadTrace();
            var (maxError, compared) = Compare(sent);
            var homeAgain = _homeAfterReset;
            ok = _clientPeer != 0 && _crateMaxTravel is > 1.5 and < MaxCrateTravel && compared > 20 && maxError < MaxDisplayError
                 && _returnedWhileGuestConnected && homeAgain == _crateHome.Count && _carriedFrames > 10 && _handError < MaxHandError;
            detail = $"clientTookIt={_clientPeer != 0} travel={_crateMaxTravel:F2} compared={compared} maxError={maxError:F3} " +
                     $"carriedFrames={_carriedFrames} handError={_handError:F3} " +
                     $"returnedAtRest={_returnedWhileGuestConnected} cratesHomeAfterReset={homeAgain}/{_crateHome.Count}";
        }
        else if (!_isObserver)
        {
            WriteTrace();
            var sawHost = _sawHost;
            ok = sawHost && _sent.Count > 20 && _crateMaxTravel is > 1.5 and < MaxCrateTravel && backToHost && _shotTookCrate
                 && _stackHangingFrames <= MaxStackHangingFrames && _carriedFrames > 10 && _handError < MaxHandError;
            detail = $"sawHost={sawHost} sent={_sent.Count} travel={_crateMaxTravel:F2} backToHost={backToHost} shotHitCrate={_shotTookCrate} " +
                     $"stackHangingFrames={_stackHangingFrames} carriedFrames={_carriedFrames} handError={_handError:F3}";
        }
        else
        {
            var sawDriver = _sawDriver;
            ok = sawDriver && _sawGuestCrate && _received.Count > 20 && _crateMaxTravel is > 1.5 and < MaxCrateTravel && backToHost;
            detail = $"sawDriver={sawDriver} sawGuestCrate={_sawGuestCrate} received={_received.Count} travel={_crateMaxTravel:F2} backToHost={backToHost}";
        }

        // Any crate, not only the one the bot throws: a held crate teleported into a stack blew the stack out of the world
        ok &= _fallen.Count == 0;
        detail += $" fellOut={_fallen.Count}";
        // An exception in a callback is logged and the game goes on: count them, or the smoke passes over them
        var errors = ErrorCounter.Smoke?.Count ?? 0;
        ok &= errors == 0;
        detail += $" errors={errors}" + (errors > 0 ? $" (first: {ErrorCounter.Smoke!.First})" : "");
        var role = _isHost ? "host" : _isObserver ? "client-b" : "client-a";
        var notWithHost = string.Join(",", _playground.GetNode("Crates").GetChildren().OfType<PlaygroundCrate>()
            .Where(crate => crate.Authority.Peer != 1)
            .Select(crate => $"{crate.Name}@{crate.Authority.Peer}(at {crate.GlobalPosition} v {crate.LinearVelocity.Length():F3} sleeping {crate.Sleeping} rest {crate.Net().RestFrames} holder {crate.ClaimedBy} frozen {crate.Freeze})"));
        GD.Print($"PLAYGROUND SMOKE role={role} ok={ok} {detail} seconds={_elapsed:F1} notWithHost={notWithHost} shots={_playground.Shots.GetChildCount()} bot={_botClock:F1}");
        GetTree().Quit(ok ? 0 : 1);
    }

    /// <summary>
    /// The largest distance between what the host displayed and what it should have displayed from the samples it had:
    /// the line between the two it received around that tick, with the values the client sent for them.
    /// <para>
    /// Measured against what arrived, not against every tick the client simulated: when packets are lost the motion
    /// in between is gone, and a bounce inside a burst is drawn as a straight line by any playback. What this catches
    /// is playback making things worse than the data - holding still through a loss and then jumping, blending in
    /// another peer's samples, drawing a crate before its state arrived.
    /// </para>
    /// </summary>
    private (double MaxError, int Compared) Compare(List<(int Tick, Vector3 Position, bool Attached)> sent)
    {
        var sentAt = sent.ToDictionary(sample => sample.Tick, sample => sample.Position);
        var attachedAt = sent.ToDictionary(sample => sample.Tick, sample => sample.Attached);
        var received = _received.Where(sentAt.ContainsKey).OrderBy(tick => tick).ToList();
        var maxError = 0.0;
        var compared = 0;
        foreach (var (tick, position) in _displayed)
        {
            var next = received.FindIndex(at => at >= tick);
            if (next <= 0) continue;
            var (fromTick, toTick) = (received[next - 1], received[next]);
            // A gap the client made on purpose - the crate at rest - has no line to follow
            if (Enumerable.Range(fromTick, toTick - fromTick).Any(at => at % NetworkObjectServer.Instance.StateIntervalTicks == 0 && !sentAt.ContainsKey(at)))
                continue;
            // Into or out of the hand there is no line to follow: playback holds until the switch, by design. While in
            // the hand the crate is measured against the drawn hand instead (handError)
            if (attachedAt[fromTick] || attachedAt[toTick]) continue;

            var expected = sentAt[fromTick].Lerp(sentAt[toTick], (float)((tick - fromTick) / (toTick - fromTick)));
            maxError = Math.Max(maxError, expected.DistanceTo(position));
            compared++;
        }
        return (maxError, compared);
    }

    private void WriteTrace()
    {
        using var file = FileAccess.Open(TracePath, FileAccess.ModeFlags.Write);
        foreach (var (tick, p, attached) in _sent)
            file.StoreLine(string.Create(CultureInfo.InvariantCulture, $"{tick},{p.X},{p.Y},{p.Z},{(attached ? 1 : 0)}"));
        // The host starts reading as soon as the file exists: this line says the client finished writing it
        file.StoreLine(TraceEnd);
    }

    private List<(int Tick, Vector3 Position, bool Attached)> ReadTrace()
    {
        var result = new List<(int, Vector3, bool)>();
        if (!FileAccess.FileExists(TracePath)) return result;
        using var file = FileAccess.Open(TracePath, FileAccess.ModeFlags.Read);
        while (!file.EofReached())
        {
            var parts = file.GetLine().Split(',');
            if (parts.Length != 5) continue;
            float F(int i) => float.Parse(parts[i], CultureInfo.InvariantCulture);
            result.Add((int.Parse(parts[0], CultureInfo.InvariantCulture), new Vector3(F(1), F(2), F(3)), parts[4] == "1"));
        }
        return result;
    }
}
