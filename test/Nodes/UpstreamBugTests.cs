using Godot;
using Netfox.Core.Logging;

namespace Netfox.Tests;

/// <summary>State that follows the tick it was simulated for, so a test can read a value back and say which tick it is.</summary>
public partial class TickStampNode : Node, IRollbackTick
{
    public int Stamp { get; set; } = -1;

    public void RollbackTick(double delta, int tick, bool isFresh) => Stamp = tick;
}

/// <summary>Carries rollback state but never simulates: upstream foxssake/netfox#564 has no coverage for this.</summary>
public partial class PassiveStateNode : Node
{
    public int Value { get; set; }
}

/// <summary>Simulates, and writes the state of a node that does not.</summary>
public partial class StampDriverNode : Node, IRollbackTick
{
    public int Stamp { get; set; } = -1;
    public PassiveStateNode Passive { get; set; } = null!;

    public void RollbackTick(double delta, int tick, bool isFresh)
    {
        Stamp = tick;
        Passive.Value = tick;
    }
}

/// <summary>
/// Reproductions for the upstream bugs triaged in #32, each answering one question: does our port do this too?
/// Every case builds its own <see cref="NetfoxContextRoot"/>, so it can pick its own settings and drive the tick loop
/// by hand instead of waiting on the clock.
/// </summary>
public partial class UpstreamBugTests : TestSuite
{
    private NetfoxSettings _settingsBackup = null!;

    public override Task BeforeCase()
    {
        _settingsBackup = NetfoxSettings.Instance;
        return Task.CompletedTask;
    }

    public override async Task AfterCase()
    {
        FreeChildren();
        NetfoxSettings.Instance = _settingsBackup;
        await NextFrame();
    }

    /// <summary>A stack of its own, with settings applied before its servers are constructed and read them.</summary>
    private async Task<NetfoxContext> Stack(string name, Action<NetfoxSettings>? configure = null)
    {
        var settings = NetfoxSettings.Load();
        configure?.Invoke(settings);
        NetfoxSettings.Instance = settings;

        var root = await Mount(new NetfoxContextRoot { Name = name });
        return root.Context;
    }

    private static void RunTo(NetfoxContext context, int toTick)
    {
        while (context.NetworkTime.Tick < toTick)
        {
            var tick = context.NetworkTime.Tick;
            context.NetworkTime.RunTick(() => context.NetworkRollback.AfterTick(tick));
            context.NetworkTime.RunAfterTickLoop();
        }
    }

    private static TickStampNode AddStamped(Node parent, string name)
    {
        var node = new TickStampNode { Name = name };
        node.AddChild(new StateNode { Name = "Input" });
        node.AddChild(new RollbackSynchronizer
        {
            Name = "RBS",
            Root = node,
            StateProperties = [":Stamp"],
            InputProperties = ["Input:TrackedValue"],
        });
        parent.AddChild(node);
        return node;
    }

    /// <summary>
    /// Upstream foxssake/netfox#570: the display offset is reported to have no visible effect while still changing the
    /// simulation. Our DisplayTick mirrors upstream, so if it reproduces it reproduces here.
    /// </summary>
    [Test]
    public async Task DisplayOffsetShowsOlderState()
    {
        const int ticks = 24;

        var live = await Stack("Live", settings => settings.DisplayOffset = 0);
        var liveNode = AddStamped((Node)live.NetworkRollback.GetParent(), "Live Subject");
        await NextFrame();
        live.NetworkTime.SetTick(0);
        live.NetworkRollback.SetTick(0);
        RunTo(live, ticks);

        var delayed = await Stack("Delayed", settings => settings.DisplayOffset = 3);
        var delayedNode = AddStamped((Node)delayed.NetworkRollback.GetParent(), "Delayed Subject");
        await NextFrame();
        delayed.NetworkTime.SetTick(0);
        delayed.NetworkRollback.SetTick(0);
        RunTo(delayed, ticks);

        Expect.Equal(0, live.NetworkRollback.DisplayOffset);
        Expect.Equal(3, delayed.NetworkRollback.DisplayOffset);

        // Both ran the same ticks; the only difference is which tick's state is left on the node after the loop
        Expect.Equal(3, liveNode.Stamp - delayedNode.Stamp,
            $"display offset 3 should show state three ticks older: live @{liveNode.Stamp}, delayed @{delayedNode.Stamp}");
    }

    /// <summary>
    /// Upstream foxssake/netfox#543: a main thread stall pushes the synchronizer past the history limit. The loop is
    /// supposed to clamp and carry on, not to go quiet and stay broken.
    /// </summary>
    [Test]
    public async Task RollbackRecoversFromAStallPastTheHistoryLimit()
    {
        var context = await Stack("Stalled");
        var node = AddStamped((Node)context.NetworkRollback.GetParent(), "Stall Subject");
        await NextFrame();

        context.NetworkTime.SetTick(0);
        context.NetworkRollback.SetTick(0);
        RunTo(context, 10);
        Expect.True(node.Stamp > 0, "the node never simulated before the stall");

        // The stall: the main thread was busy, and the clock jumped far past the history limit in one step
        var limit = context.NetworkRollback.HistoryLimit;
        var resumeAt = context.NetworkTime.Tick + limit * 4;
        context.NetworkTime.SetTick(resumeAt);

        var messages = new List<string>();
        using (CaptureLog(messages))
        {
            RunTo(context, resumeAt + 20);

            // What a stall actually delivers: packets for the ticks that went by while the main thread was busy
            var stale = new Snapshot(11);
            stale.SetProperty(node, ":Stamp", 11);
            stale.SetAuth(node, true);
            context.NetworkHistoryServer.MergeRollbackState(stale);
        }

        // Clamped, not skipped: the node is simulating again, on ticks from after the stall
        Expect.True(node.Stamp >= resumeAt,
            $"the node stopped simulating after the stall: stamped @{node.Stamp}, resumed at @{resumeAt}");

        // And the state that arrived too late is dropped with a word about it, rather than silently corrupting history
        Expect.True(messages.Any(message => message.Contains("too old")),
            $"a state from before the stall should be dropped out loud; captured {messages.Count} lines");
    }

    /// <summary>
    /// Upstream foxssake/netfox#514: the history limit warnings name a tick but not the node, which is the one thing
    /// you need to act on them.
    /// </summary>
    [Test]
    public async Task HistoryLimitWarningsNameTheSubject()
    {
        var context = await Stack("Named");
        var node = AddStamped((Node)context.NetworkRollback.GetParent(), "Named Subject");
        await NextFrame();

        context.NetworkTime.SetTick(200);
        context.NetworkRollback.SetTick(200);
        RunTo(context, 210);

        var messages = new List<string>();
        using (CaptureLog(messages))
        {
            // State for a tick long past: the merge has to drop it, and say for whom
            var snapshot = new Snapshot(1);
            snapshot.SetProperty(node, ":Stamp", 42);
            snapshot.SetAuth(node, true);
            context.NetworkHistoryServer.MergeRollbackState(snapshot);
        }

        var warning = messages.FirstOrDefault(message => message.Contains("too old"));
        Expect.NotNull(warning, $"the stale merge should warn; captured {messages.Count} lines");
        Expect.True(warning!.Contains("Named Subject"),
            $"the warning should name the subject, got: {warning}");
    }

    /// <summary>
    /// Upstream foxssake/netfox#564: nodes that carry rollback state but never simulate had no coverage. Their state
    /// still has to be recorded per tick and restored on a rollback, like that of the node driving them.
    /// </summary>
    [Test]
    public async Task NonSimulatedNodeWithStateIsRecordedAndRestored()
    {
        var context = await Stack("Passive");
        var parent = (Node)context.NetworkRollback.GetParent();

        // The driver simulates; the passive child only holds state, the way a score or an ammo counter would
        var driver = new StampDriverNode { Name = "Driver" };
        driver.Passive = new PassiveStateNode { Name = "Passive" };
        driver.AddChild(driver.Passive);
        driver.AddChild(new StateNode { Name = "Input" });
        driver.AddChild(new RollbackSynchronizer
        {
            Name = "RBS",
            Root = driver,
            StateProperties = [":Stamp", "Passive:Value"],
            InputProperties = ["Input:TrackedValue"],
        });
        parent.AddChild(driver);
        await NextFrame();

        context.NetworkTime.SetTick(0);
        context.NetworkRollback.SetTick(0);
        RunTo(context, 12);

        Expect.True(context.RollbackSimulationServer.RegisteredNodes.Contains(driver), "the driver should be simulated");
        Expect.False(context.RollbackSimulationServer.RegisteredNodes.Contains(driver.Passive),
            "the passive node has no rollback tick and must not be simulated");

        // Its history is there to roll back to all the same
        Expect.True(context.NetworkHistoryServer.RestoreRollbackState(6),
            "there should be recorded state to restore for a non-simulated node");
        Expect.Equal(5, driver.Passive.Value,
            $"tick 6 should hold the value written while simulating tick 5, got {driver.Passive.Value}");
        Expect.Equal(5, driver.Stamp, "the driver's own state should restore to the same tick");
    }

    /// <summary>
    /// Upstream foxssake/netfox#621: a node unparented and then freed used to leave orphaned keys behind, because the
    /// synchronizer skipped cleanup for what looked like a reparent.
    /// </summary>
    [Test]
    public async Task UnparentedThenFreedNodeLeavesNothingBehind()
    {
        var context = await Stack("Orphans");
        var parent = (Node)context.NetworkRollback.GetParent();

        var keeper = AddStamped(parent, "Keeper");
        var doomed = AddStamped(parent, "Doomed");
        await NextFrame();

        context.NetworkTime.SetTick(0);
        context.NetworkRollback.SetTick(0);
        RunTo(context, 8);

        var registered = context.NetworkSynchronizationServer.OwnedRollbackStateProperties.Subjects.Count;
        Expect.True(registered >= 2, $"both subjects should be registered, got {registered}");

        // Unparent first, then free: the two steps upstream's cleanup falls between
        parent.RemoveChild(doomed);
        await NextFrame();
        doomed.Free();
        await NextFrame();

        // The tick loop has to keep running over the survivor without tripping over the freed node
        RunTo(context, 20);

        Expect.True(keeper.Stamp >= 18, $"the surviving node stopped simulating at @{keeper.Stamp}");

        foreach (var subject in context.NetworkSynchronizationServer.OwnedRollbackStateProperties.Subjects)
            Expect.True(GodotObject.IsInstanceValid(subject), "a freed node is still registered for synchronization");
        foreach (var subject in context.RollbackSimulationServer.RegisteredNodes)
            Expect.True(GodotObject.IsInstanceValid(subject), "a freed node is still registered for simulation");
    }

    /// <summary>Redirects the logger for the duration of the block, so a case can assert on what was logged.</summary>
    private static IDisposable CaptureLog(List<string> messages) => new LogCapture(messages);

    private sealed class LogCapture : IDisposable
    {
        private readonly Action<string> _print;
        private readonly LogLevel _level;
        private readonly LogLevel _moduleLevel;

        public LogCapture(List<string> messages)
        {
            _print = NetfoxLogger.Print;
            _level = NetfoxLogger.Level;
            _moduleLevel = NetfoxLogger.ModuleLevels.GetValueOrDefault("netfox", NetfoxLogger.DefaultLogLevel);

            NetfoxLogger.Print = messages.Add;
            NetfoxLogger.Level = LogLevel.Trace;
            NetfoxLogger.ModuleLevels["netfox"] = LogLevel.Trace;
        }

        public void Dispose()
        {
            NetfoxLogger.Print = _print;
            NetfoxLogger.Level = _level;
            NetfoxLogger.ModuleLevels["netfox"] = _moduleLevel;
        }
    }
}
