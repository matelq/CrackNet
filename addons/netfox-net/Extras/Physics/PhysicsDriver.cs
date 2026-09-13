using Godot;
using Netfox.Core.Logging;

namespace Netfox.Extras;

/// <summary>
/// Steps physics in time with netfox ticks and snapshots the physics space so it can take part in rollback.
/// Subclasses bind to a concrete physics engine. Port of netfox.extras/physics/physics_driver.gd.
/// </summary>
public partial class PhysicsDriver : Node
{
    /// <summary>The netfox stack this node uses; resolved when it enters the tree.</summary>
    public NetfoxContext Context { get; private set; } = NetfoxContext.Default;
    public const string NetworkRigidBodyGroup = "network_rigid_body";

    protected static readonly NetfoxLogger Logger = NetfoxLogger.ForExtras("PhysicsDriver");

    /// <summary>Physics steps per network tick.</summary>
    [Export] public int PhysicsFactor { get; set; } = 2;

    /// <summary>Snapshot and roll back the entire physics space.</summary>
    [Export] public bool RollbackPhysicsSpace { get; set; } = true;

    protected Rid PhysicsSpace;

    public override void _EnterTree()
    {
        Context = NetfoxContext.For(this);
        var time = Context.NetworkTime;
        time.BeforeTick += BeforeTick;
        time.AfterTickLoop += AfterTickLoop;

        var rollback = Context.NetworkRollback;
        if (RollbackPhysicsSpace) rollback.OnPrepareTick += OnPrepareTick;
        rollback.OnProcessTick += OnProcessTick;
    }

    public override void _ExitTree()
    {
        if (Context.NetworkTime is { } time)
        {
            time.BeforeTick -= BeforeTick;
            time.AfterTickLoop -= AfterTickLoop;
        }
        if (Context.NetworkRollback is { } rollback)
        {
            rollback.OnPrepareTick -= OnPrepareTick;
            rollback.OnProcessTick -= OnProcessTick;
        }
    }

    public override void _Ready() => InitPhysicsSpace();

    private void BeforeTick(double delta, int tick)
    {
        SnapshotSpace(tick);
        StepPhysics(delta, tick);
    }

    private void OnPrepareTick(int tick)
    {
        if (Context.NetworkRollback.RollbackFrom == tick)
            RollbackSpace(tick); // First tick of the rollback loop, rewind
        else
            SnapshotSpace(tick); // Subsequent ticks rewrite history
    }

    private void OnProcessTick(int tick) => StepPhysics(Context.NetworkTime.Ticktime, tick);

    private void AfterTickLoop() => TrimSnapshots(Context.NetworkRollback.HistoryStart);

    /// <summary>Steps physics for one tick, split into PhysicsFactor sub-steps, ticking NetworkRigidBody nodes in between.</summary>
    public void StepPhysics(double delta, int tick)
    {
        var fracDelta = delta / PhysicsFactor;
        var participants = GetTree().GetNodesInGroup(NetworkRigidBodyGroup);
        for (var i = 0; i < PhysicsFactor; i++)
        {
            foreach (var participant in participants)
                if (participant is INetworkRigidBody body)
                    body.PhysicsRollbackTick(fracDelta, tick);

            PhysicsStep(fracDelta);
        }
    }

    protected virtual void InitPhysicsSpace() { }
    protected virtual void PhysicsStep(double delta) { }
    protected virtual void SnapshotSpace(int tick) { }
    protected virtual void RollbackSpace(int tick) { }

    /// <summary>Drops snapshots older than the rollback history. Drivers whose engine keeps its own cache do nothing.</summary>
    protected virtual void TrimSnapshots(int historyStart) { }
}

/// <summary>
/// A driver that keeps the snapshots itself, as body states per Rid per tick. The Rapier drivers do not: the extension
/// holds its own rolling cache, which is why the snapshot storage lives here and not in <see cref="PhysicsDriver"/>.
/// </summary>
public abstract partial class BodyStatePhysicsDriver : PhysicsDriver
{
    protected readonly SortedDictionary<int, Dictionary<Rid, Godot.Collections.Array>> Snapshots = new();

    protected override void TrimSnapshots(int historyStart)
    {
        foreach (var tick in Snapshots.Keys.Where(tick => tick < historyStart).ToList())
            Snapshots.Remove(tick);
    }
}

/// <summary>Physics bodies driven by a PhysicsDriver during rollback.</summary>
public interface INetworkRigidBody
{
    void PhysicsRollbackTick(double delta, int tick);
}
