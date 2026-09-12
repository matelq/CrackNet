using Godot;
using Netfox.Core.Logging;

namespace Netfox.Tests;

/// <summary>State that advances on its own every tick, so a resimulation recomputes it and overwrites what was written in.</summary>
public partial class VictimNode : Node, IRollbackTick
{
    public int TrackedValue { get; set; }

    public void RollbackTick(double delta, int tick, bool isFresh) => TrackedValue += 1;
}

/// <summary>A node that pushes another one once, on a given tick, the way a shove or a moving platform would.</summary>
public partial class MutatorNode : Node, IRollbackTick
{
    public VictimNode? Target { get; set; }
    public int PushAtTick { get; set; } = -1;

    /// <summary>Upstream's own example recomputes the shove on every resimulation; guarding it with isFresh does not.</summary>
    public bool OnlyWhenFresh { get; set; }
    public int PushAmount { get; set; } = 100;
    public int Pushes { get; private set; }

    /// <summary>State of its own, so the synchronizer has something to record for this node.</summary>
    public int Ticks { get; set; }

    public void RollbackTick(double delta, int tick, bool isFresh)
    {
        Ticks++;
        if (Target is null || tick != PushAtTick) return;

        if (OnlyWhenFresh && !isFresh) return;

        Target.TrackedValue += PushAmount;
        NetworkRollback.Instance.Mutate(Target);
        Pushes++;
    }
}

/// <summary>
/// Upstream foxssake/netfox#383: a node mutates another node's state, then something forces a resimulation from before
/// the mutation, and the victim simulates over the mutated state and loses it.
/// </summary>
public partial class MutationTests : TestSuite
{
    private VictimNode _victim = null!;
    private MutatorNode _mutator = null!;

    public override async Task BeforeCase()
    {
        NetworkTime.Instance.SetTick(0);
        NetworkRollback.Instance.SetTick(0);

        _victim = new VictimNode { Name = "Victim" };
        _victim.AddChild(new StateNode { Name = "Input" });
        _victim.AddChild(new RollbackSynchronizer
        {
            Name = "Victim RBS",
            Root = _victim,
            StateProperties = [":TrackedValue"],
            InputProperties = ["Input:TrackedValue"],
        });

        _mutator = new MutatorNode { Name = "Mutator", PushAtTick = 8, Target = _victim };
        _mutator.AddChild(new StateNode { Name = "Input" });
        _mutator.AddChild(new RollbackSynchronizer
        {
            Name = "Mutator RBS",
            Root = _mutator,
            StateProperties = [":Ticks"],
            InputProperties = ["Input:TrackedValue"],
        });

        await Mount(_victim);
        await Mount(_mutator);
        await NextFrame();
    }

    public override Task AfterCase()
    {
        FreeChildren();
        return Task.CompletedTask;
    }

    /// <summary>Runs the tick loop up to <paramref name="toTick"/>, the way NetworkTime does: tick, record input, roll back.</summary>
    private static void RunTo(int toTick)
    {
        while (NetworkTime.Instance.Tick < toTick)
        {
            var tick = NetworkTime.Instance.Tick;
            NetworkTime.Instance.RunTick(() => NetworkRollback.Instance.AfterTick(tick));
            NetworkTime.Instance.RunAfterTickLoop();
        }
    }

    /// <summary>Asks for one resimulation from <paramref name="tick"/>, the way a synchronizer does on spawn.</summary>
    private static void RequestResimulationFrom(int tick)
    {
        Action? handler = null;
        handler = () =>
        {
            NetworkRollback.Instance.NotifyResimulationStart(tick);
            NetworkRollback.Instance.BeforeLoop -= handler;
        };
        NetworkRollback.Instance.BeforeLoop += handler;
    }

    [Test]
    public void MutationIsAppliedWhileSimulatingForward()
    {
        RunTo(10);

        Expect.Equal(1, _mutator.Pushes);
        Expect.Equal(10 + 100, _victim.TrackedValue);
    }

    [Test]
    public void MutationRecomputedOnResimulationSurvives()
    {
        RunTo(10);
        Expect.Equal(10 + 100, _victim.TrackedValue);

        // Something arrives late for a tick before the mutation, so the loop resimulates across it. The request only
        // counts while the loop is starting, which is when the synchronizers make theirs.
        RequestResimulationFrom(7);
        RunTo(11);

        // The push is a function of the tick, so resimulating it puts the same value back
        Expect.Equal(11 + 100, _victim.TrackedValue);
    }

    [Test]
    public void MutationAppliedOnlyWhenFreshIsLostOnResimulation()
    {
        _mutator.OnlyWhenFresh = true;

        RunTo(10);
        Expect.Equal(10 + 100, _victim.TrackedValue);

        RequestResimulationFrom(7);
        RunTo(11);

        // Guarded with isFresh, the push never runs again, and the victim simulates over the value it was given
        Expect.Equal(1, _mutator.Pushes);
        Expect.Equal(11, _victim.TrackedValue);
    }

    [Test]
    public void LostMutationIsTraced()
    {
        _mutator.OnlyWhenFresh = true;

        var messages = new List<string>();
        var print = NetfoxLogger.Print;
        var level = NetfoxLogger.Level;
        var moduleLevel = NetfoxLogger.ModuleLevels.GetValueOrDefault("netfox", NetfoxLogger.DefaultLogLevel);

        NetfoxLogger.Print = messages.Add;
        NetfoxLogger.Level = LogLevel.Trace;
        NetfoxLogger.ModuleLevels["netfox"] = LogLevel.Trace;

        try
        {
            RunTo(10);
            RequestResimulationFrom(7);
            RunTo(11);
        }
        finally
        {
            NetfoxLogger.Print = print;
            NetfoxLogger.Level = level;
            NetfoxLogger.ModuleLevels["netfox"] = moduleLevel;
        }

        Expect.True(messages.Any(message => message.Contains("was not reproduced while resimulating")),
            $"the dropped mutation should leave a trace line to debug with; captured {messages.Count} lines");
    }
}
