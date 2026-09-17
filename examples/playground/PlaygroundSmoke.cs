using System.Globalization;
using Godot;
using FileAccess = Godot.FileAccess;

namespace Netfox.Examples.Playground;

/// <summary>
/// Drives the playground headless and checks it. Run a host, client A, then client B; the host must outlive both:
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
/// </summary>
public partial class PlaygroundSmoke : Node
{
    private const string TracePath = "user://playground-smoke-client.csv";
    private const string CrateName = "Crate0";
    private const double MaxDisplayError = 0.25;

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

    /// <summary>The arena is 40 by 40: a crate farther than this from its start has been blown out of the world.</summary>
    private const double MaxCrateTravel = 45;
    private int _clientPeer;
    private bool _sawGuestCrate;

    private readonly List<(int Tick, Vector3 Position)> _sent = new();
    private readonly List<(double Tick, Vector3 Position)> _displayed = new();
    private readonly HashSet<int> _received = new();

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        _isHost = args.Contains("--host");
        _isObserver = args.Contains("--smoke-client=b");
        foreach (var arg in args)
            if (arg.StartsWith("--seconds=")) _seconds = double.Parse(arg["--seconds=".Length..], CultureInfo.InvariantCulture);

        _playground = GetParent<Playground>();
        _crate = _playground.GetNode<PlaygroundCrate>($"Crates/{CrateName}");
        _crateStart = _crate.GlobalPosition;
        _target = _playground.GetNode<PlaygroundCrate>("Crates/Crate1");
        PlaygroundPlayer.Bot = !_isHost && !_isObserver ? Drive : _ => default;
        // A trace left by an earlier run would be compared against this one's ticks
        if (!_isHost && FileAccess.FileExists(TracePath)) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(TracePath));
        _crate.Object.Diagnostics.SampleSent += RecordSent;
        _onTop = _playground.GetNode<PlaygroundCrate>("Crates/Crate4");
        // Handing everything back when a guest leaves also returns the crate to the host, so only a return while the
        // thrower is still here proves the crate came back because it came to rest
        _crate.Object.AuthorityChanged += () =>
        {
            if (_isHost && _crate.Object.Authority == 1 && _clientPeer != 0 && Multiplayer.GetPeers().Contains(_clientPeer))
                _returnedWhileGuestConnected = true;
        };
        _crate.Object.Diagnostics.SampleReceived += tick => _received.Add(tick);
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
            _target = crates
                .Where(crate => crate.Object.Authority == 1 && crate.GlobalPosition.Y < 1.2f)
                .Where(crate => crates.All(other => other == crate || !Blocks(me.GlobalPosition, crate.GlobalPosition, other.GlobalPosition)))
                .OrderBy(crate => crate.GlobalPosition.DistanceTo(me.GlobalPosition))
                .FirstOrDefault() ?? _target;
            foreach (var crate in crates)
                crate.Object.AuthorityChanged += () =>
                    _shotTookCrate |= crate.Object.IsAuthority && crate.Object.SpreadCause.Contains("/Shots/");
        }
        return (FlatTo(_target, me) * 0.05f, false, false, false);
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
            < 13.1 => Aim(me),
            < 13.2 => (Vector3.Zero, false, false, true),
            _ => default,
        };
    }

    /// <summary>On the client, what it sent for the crate while simulating it: exactly the samples that went out.</summary>
    private void RecordSent(int stateTick)
    {
        if (!_isHost) _sent.Add((stateTick, _crate.GlobalPosition));
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_botClock >= 0) _botClock += delta;
    }

    public override void _Process(double delta)
    {
        _elapsed += delta;
        _crateMaxTravel = Math.Max(_crateMaxTravel, _crate.GlobalPosition.DistanceTo(_crateStart));

        // Crate4 stood on Crate0. Once Crate0 has been taken from under it, a Crate4 still up there and frozen here is
        // hanging over nothing, waiting for the host's word that it fell
        var underneathGone = new Vector2(_crate.GlobalPosition.X - _onTop.GlobalPosition.X, _crate.GlobalPosition.Z - _onTop.GlobalPosition.Z).Length() > 1.0f;
        if (!_isHost && !_isObserver && underneathGone && _onTop.GlobalPosition.Y > 1.2f && _onTop.Freeze && !_onTop.Object.IsAuthority)
            _stackHangingFrames++;

        if (_isHost && !_crate.Object.IsAuthority)
        {
            _clientPeer = _crate.Object.Authority;
            if (_crate.Object.Diagnostics.DisplayTick is { } shown && _crate.Visible)
                _displayed.Add((shown, _crate.GlobalPosition));
        }
        if (_isObserver && _crate.Object.Authority is not 1 && !_crate.Object.IsAuthority && _crate.Visible)
            _sawGuestCrate = true;

        if (_elapsed >= _seconds) Finish();
    }

    private void Finish()
    {
        SetProcess(false);
        var backToHost = _crate.Object.Authority == 1;
        bool ok;
        string detail;

        if (_isHost)
        {
            var sent = ReadTrace();
            var (maxError, compared) = Compare(sent);
            ok = _clientPeer != 0 && _crateMaxTravel is > 1.5 and < MaxCrateTravel && compared > 20 && maxError < MaxDisplayError
                 && _returnedWhileGuestConnected;
            detail = $"clientTookIt={_clientPeer != 0} travel={_crateMaxTravel:F2} compared={compared} maxError={maxError:F3} returnedAtRest={_returnedWhileGuestConnected}";
        }
        else if (!_isObserver)
        {
            WriteTrace();
            var sawHost = _playground.Players.GetNodeOrNull<PlaygroundPlayer>("Player1") is { Visible: true };
            ok = sawHost && _sent.Count > 20 && _crateMaxTravel is > 1.5 and < MaxCrateTravel && backToHost && _shotTookCrate
                 && _stackHangingFrames <= MaxStackHangingFrames;
            detail = $"sawHost={sawHost} sent={_sent.Count} travel={_crateMaxTravel:F2} backToHost={backToHost} shotHitCrate={_shotTookCrate} stackHangingFrames={_stackHangingFrames}";
        }
        else
        {
            var sawDriver = _playground.Players.GetChildren().OfType<PlaygroundPlayer>()
                .Any(player => player.Peer != 1 && player.Peer != Multiplayer.GetUniqueId() && player.Visible);
            ok = sawDriver && _sawGuestCrate && _received.Count > 20 && _crateMaxTravel is > 1.5 and < MaxCrateTravel && backToHost;
            detail = $"sawDriver={sawDriver} sawGuestCrate={_sawGuestCrate} received={_received.Count} travel={_crateMaxTravel:F2} backToHost={backToHost}";
        }

        var role = _isHost ? "host" : _isObserver ? "client-b" : "client-a";
        GD.Print($"PLAYGROUND SMOKE role={role} ok={ok} {detail}");
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
    private (double MaxError, int Compared) Compare(List<(int Tick, Vector3 Position)> sent)
    {
        var sentAt = sent.ToDictionary(sample => sample.Tick, sample => sample.Position);
        var received = _received.Where(sentAt.ContainsKey).OrderBy(tick => tick).ToList();
        var maxError = 0.0;
        var compared = 0;
        foreach (var (tick, position) in _displayed)
        {
            var next = received.FindIndex(at => at >= tick);
            if (next <= 0) continue;
            var (fromTick, toTick) = (received[next - 1], received[next]);
            // A gap the client made on purpose - the crate at rest - has no line to follow
            if (Enumerable.Range(fromTick, toTick - fromTick).Any(at => at % NetworkObjectServer.StateIntervalTicks == 0 && !sentAt.ContainsKey(at)))
                continue;

            var expected = sentAt[fromTick].Lerp(sentAt[toTick], (float)((tick - fromTick) / (toTick - fromTick)));
            maxError = Math.Max(maxError, expected.DistanceTo(position));
            compared++;
        }
        return (maxError, compared);
    }

    private void WriteTrace()
    {
        using var file = FileAccess.Open(TracePath, FileAccess.ModeFlags.Write);
        foreach (var (tick, p) in _sent)
            file.StoreLine(string.Create(CultureInfo.InvariantCulture, $"{tick},{p.X},{p.Y},{p.Z}"));
    }

    private static List<(int Tick, Vector3 Position)> ReadTrace()
    {
        var result = new List<(int, Vector3)>();
        if (!FileAccess.FileExists(TracePath)) return result;
        using var file = FileAccess.Open(TracePath, FileAccess.ModeFlags.Read);
        while (!file.EofReached())
        {
            var parts = file.GetLine().Split(',');
            if (parts.Length != 4) continue;
            float F(int i) => float.Parse(parts[i], CultureInfo.InvariantCulture);
            result.Add((int.Parse(parts[0], CultureInfo.InvariantCulture), new Vector3(F(1), F(2), F(3))));
        }
        return result;
    }
}
