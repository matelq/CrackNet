using Godot;
using FileAccess = Godot.FileAccess;

namespace Netfox.Examples;

/// <summary>The input the parity player reads. See <see cref="ParityPlayer"/> on why it is set per tick.</summary>
public partial class ParityInput : Node
{
    public Vector3 Movement { get; set; }
}

/// <summary>
/// Parity trace, C# side. The GDScript counterpart is parity/parity.gd and has to stay identical: same scene shape,
/// same inputs, same settings, same arithmetic, written the same way round so the floats match.
/// <para>
/// One offline peer, so nothing here depends on packet timing: every tick's state is a pure function of the tick
/// number, whatever the frame rate chunks the ticks into. Input is set from BeforeTick rather than from a
/// <c>BaseNetInput</c>, because Gather runs once per tick loop and would otherwise hand the same input to several
/// ticks depending on how a frame happened to land.
/// </para>
/// </summary>
public partial class ParityPlayer : Node3D, IRollbackTick
{
    private const int Ticks = 300;
    private const float Speed = 4.0f;
    private const string DefaultTracePath = "user://parity-csharp.csv";

    private string _tracePath = DefaultTracePath;

    /// <summary>Rollback state, alongside the position: an integer that cannot drift the way a float can.</summary>
    public int Counter { get; set; }

    private ParityInput _input = null!;
    private readonly Dictionary<int, (int Counter, float X, float Z)> _trace = new();
    private readonly Dictionary<int, int> _sims = new();
    private bool _done;

    private static string Arg(string prefix, string fallback)
        => OS.GetCmdlineUserArgs().FirstOrDefault(arg => arg.StartsWith(prefix))?[prefix.Length..] ?? fallback;

    public static Vector3 MovementFor(int tick) => tick / 5 % 2 == 0 ? Vector3.Right : Vector3.Back;

    /// <summary>Builds the same tree parity/parity.tscn describes on the GDScript side.</summary>
    public static ParityPlayer Build(Node parent)
    {
        var player = new ParityPlayer { Name = "Parity" };
        player._input = new ParityInput { Name = "Input" };
        player.AddChild(player._input);
        player.AddChild(new RollbackSynchronizer
        {
            Name = "RollbackSynchronizer",
            Root = player,
            StateProperties = [":Counter", ":position"],
            InputProperties = ["Input:Movement"],
        });
        parent.AddChild(player);
        return player;
    }

    public override async void _Ready()
    {
        _tracePath = Arg("--trace=", DefaultTracePath);
        Multiplayer.MultiplayerPeer = new OfflineMultiplayerPeer();

        // The synchronizer registers its nodes from a deferred call, so starting the clock in the same frame is a
        // race: whichever runs first decides whether tick 0 is simulated at all. Let the scene settle, then start.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        NetworkTime.Instance.BeforeTick += SetInput;
        NetworkTime.Instance.AfterTickLoop += CheckDone;
        NetworkTime.Instance.Start();
    }

    private void SetInput(double delta, int tick) => _input.Movement = MovementFor(tick);

    public void RollbackTick(double delta, int tick, bool isFresh)
    {
        Counter += _input.Movement.X > 0.0f ? 1 : 2;
        Position += _input.Movement * Speed * (float)delta;

        _trace[tick] = (Counter, Position.X, Position.Z);
        _sims[tick] = _sims.GetValueOrDefault(tick) + 1;
    }

    private void CheckDone()
    {
        if (_done || NetworkTime.Instance.Tick < Ticks) return;
        _done = true;
        WriteTrace();
        GetTree().Quit(0);
    }

    private void WriteTrace()
    {
        using var file = FileAccess.Open(_tracePath, FileAccess.ModeFlags.Write);
        file.StoreLine("tick,counter,x,z,sims");

        foreach (var tick in _trace.Keys.Order())
        {
            var row = _trace[tick];
            file.StoreLine(FormattableString.Invariant(
                $"{tick},{row.Counter},{row.X:F6},{row.Z:F6},{_sims[tick]}"));
        }

        GD.Print($"PARITY RESULT role=csharp ticks={_trace.Count} trace={ProjectSettings.GlobalizePath(_tracePath)}");
    }
}

/// <summary>Scene root: builds the parity player and lets it run. Mirrors parity/parity.tscn.</summary>
public partial class Parity : Node
{
    public override void _Ready() => ParityPlayer.Build(this);
}
