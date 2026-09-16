using System.Globalization;
using Godot;
using FileAccess = Godot.FileAccess;

namespace Netfox.Examples.Playground;

/// <summary>
/// Drives the playground headless and checks it. Run the host first, it has to outlive the client:
///   godot --headless --path . res://examples/playground/playground.tscn -- --smoke --host --seconds=22
///   godot --headless --path . res://examples/playground/playground.tscn -- --smoke --join --seconds=14
/// Both under the same network profile (the project setting, or --profile=name on both).
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
    private double _seconds = 12;
    private double _elapsed;
    private double _botClock = -1;

    private Playground _playground = null!;
    private PlaygroundCrate _crate = null!;
    private Vector3 _crateStart;
    private double _crateMaxTravel;
    private int _clientPeer;

    private readonly List<(int Tick, Vector3 Position)> _sent = new();
    private readonly List<(double Tick, Vector3 Position)> _displayed = new();
    private readonly HashSet<int> _received = new();

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        _isHost = args.Contains("--host");
        foreach (var arg in args)
            if (arg.StartsWith("--seconds=")) _seconds = double.Parse(arg["--seconds=".Length..], CultureInfo.InvariantCulture);

        _playground = GetParent<Playground>();
        _crate = _playground.GetNode<PlaygroundCrate>($"Crates/{CrateName}");
        _crateStart = _crate.GlobalPosition;
        PlaygroundPlayer.Bot = _isHost ? _ => default : Drive;
        // A trace left by an earlier run would be compared against this one's ticks
        if (!_isHost && FileAccess.FileExists(TracePath)) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(TracePath));
        _crate.Object.SampleSent += RecordSent;
        _crate.Object.SampleReceived += tick => _received.Add(tick);
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

        if (_isHost && !_crate.Object.IsAuthority)
        {
            _clientPeer = _crate.Object.Authority;
            if (NetworkObjectServer.Instance.GetDisplayTick(_clientPeer) is { } shown && _crate.Visible)
                _displayed.Add((shown, _crate.GlobalPosition));
        }

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
            ok = _clientPeer != 0 && _crateMaxTravel > 1.5 && compared > 20 && maxError < MaxDisplayError && backToHost;
            detail = $"clientTookIt={_clientPeer != 0} travel={_crateMaxTravel:F2} compared={compared} maxError={maxError:F3} backToHost={backToHost}";
        }
        else
        {
            WriteTrace();
            var sawHost = _playground.Players.GetNodeOrNull<PlaygroundPlayer>("Player1") is { Visible: true };
            ok = sawHost && _sent.Count > 20 && _crateMaxTravel > 1.5 && backToHost;
            detail = $"sawHost={sawHost} sent={_sent.Count} travel={_crateMaxTravel:F2} backToHost={backToHost}";
        }

        GD.Print($"PLAYGROUND SMOKE role={(_isHost ? "host" : "client")} ok={ok} {detail}");
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
