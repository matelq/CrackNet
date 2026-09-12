using System.Diagnostics;
using Godot;

namespace Netfox.Tests;

/// <summary>
/// Measurements for the hot path items in #17, so the work goes where the time actually is. Prints numbers, asserts
/// only what must not regress.
/// </summary>
public partial class HotPathBenchmarkTests : TestSuite
{
    public override Task AfterCase()
    {
        FreeChildren();
        return Task.CompletedTask;
    }

    [Test]
    public async Task AuthorityCheckCost()
    {
        var node = await Mount(new Node3D { Name = "Authority Subject" });
        const int iterations = 200_000;

        for (var i = 0; i < 1000; i++) _ = node.IsMultiplayerAuthority();

        var stopwatch = Stopwatch.StartNew();
        var owned = 0;
        for (var i = 0; i < iterations; i++)
            if (node.IsMultiplayerAuthority()) owned++;
        var elapsed = stopwatch.Elapsed.TotalMilliseconds;

        GD.Print(FormattableString.Invariant($"AUTHORITY {iterations} IsMultiplayerAuthority calls: {elapsed:F1}ms ({owned} owned)"));
    }

    [Test]
    public async Task GroupWalkCost()
    {
        var group = new StringName("__nf_bench_group");
        var parent = await Mount(new Node { Name = "Group Parent" });
        for (var i = 0; i < 16; i++)
        {
            var child = new Node { Name = $"Member{i}" };
            parent.AddChild(child);
            child.AddToGroup(group);
        }

        const int iterations = 20_000;
        for (var i = 0; i < 100; i++) _ = GetTree().GetNodesInGroup(group);

        var stopwatch = Stopwatch.StartNew();
        var seen = 0;
        for (var i = 0; i < iterations; i++)
            seen += GetTree().GetNodesInGroup(group).Count;
        var elapsed = stopwatch.Elapsed.TotalMilliseconds;

        GD.Print(FormattableString.Invariant(
            $"GROUP WALK {iterations} GetNodesInGroup calls over 16 members: {elapsed:F1}ms ({seen} nodes seen)"));
    }

    [Test]
    public async Task AllocationsPerSimulatedTick()
    {
        var subject = new StateNode { Name = "Alloc Subject" };
        subject.AddChild(new StateNode { Name = "Input" });
        subject.AddChild(new RollbackSynchronizer
        {
            Name = "Alloc RBS",
            Root = subject,
            StateProperties = [":TrackedValue"],
            InputProperties = ["Input:TrackedValue"],
        });
        await Mount(subject);
        await NextFrame();

        NetworkTime.Instance.SetTick(0);
        NetworkRollback.Instance.SetTick(0);

        // Warm up: first ticks allocate buffers that later ticks reuse
        for (var i = 0; i < 20; i++) RunOneTick();

        const int ticks = 100;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < ticks; i++) RunOneTick();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        GD.Print(FormattableString.Invariant(
            $"ALLOCATIONS {ticks} ticks with one rollback subject: {allocated / 1024.0:F1}KB total, {allocated / (double)ticks:F0}B per tick"));
    }

    [Test]
    public async Task AllocationsOfTheUsualSuspects()
    {
        var group = new StringName("__nf_alloc_group");
        var parent = await Mount(new Node { Name = "Alloc Group Parent" });
        for (var i = 0; i < 8; i++)
        {
            var child = new Node { Name = $"Member{i}" };
            parent.AddChild(child);
            child.AddToGroup(group);
        }

        GD.Print(FormattableString.Invariant($"ALLOC GetNodesInGroup: {PerCall(() => GetTree().GetNodesInGroup(group))}B"));
        GD.Print(FormattableString.Invariant($"ALLOC Multiplayer.GetPeers: {PerCall(() => Multiplayer.GetPeers())}B"));
        GD.Print(FormattableString.Invariant($"ALLOC new Snapshot: {PerCall(() => new Snapshot(1))}B"));
    }

    [Test]
    public async Task SerializerAllocations()
    {
        var subject = await Mount(new StateNode { Name = "Serialized Subject" });
        NetworkIdentityServer.Instance.RegisterNode(subject);

        var properties = new PropertyPool();
        properties.Add(subject, "TrackedValue");

        var snapshot = new Snapshot(7);
        snapshot.SetProperty(subject, "TrackedValue", 42);
        snapshot.SetAuth(subject, true);

        var serializer = new DenseSnapshotSerializer(new NetworkSchema(NetworkSchemas.Variant()));

        GD.Print(FormattableString.Invariant(
            $"ALLOC DenseSnapshotSerializer.WriteFor: {PerCall(() => serializer.WriteFor(2, snapshot, properties))}B per call"));
    }

    private static double PerCall(Func<object> body, int iterations = 2000)
    {
        for (var i = 0; i < 100; i++) body();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++) body();
        return (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iterations;
    }

    private static void RunOneTick()
    {
        var tick = NetworkTime.Instance.Tick;
        NetworkTime.Instance.RunTick(() => NetworkRollback.Instance.AfterTick(tick));
        NetworkTime.Instance.RunAfterTickLoop();
    }
}
