using Godot;
using Netfox.Core.Logging;

namespace Netfox.Extras;

/// <summary>
/// Steps physics in time with netfox ticks and snapshots the physics space so it can take part in rollback.
/// Subclasses bind to a concrete physics engine. Port of netfox.extras/physics/physics_driver.gd.
/// </summary>
public partial class PhysicsDriver : Node
{
    public const string NetworkRigidBodyGroup = "network_rigid_body";

    protected static readonly NetfoxLogger Logger = NetfoxLogger.ForExtras("PhysicsDriver");

    /// <summary>Physics steps per network tick.</summary>
    [Export] public int PhysicsFactor { get; set; } = 2;

    /// <summary>Snapshot and roll back the entire physics space.</summary>
    [Export] public bool RollbackPhysicsSpace { get; set; } = true;

    protected Rid PhysicsSpace;
    protected readonly SortedDictionary<int, object> Snapshots = new();

    public override void _EnterTree()
    {
        var time = NetworkTime.Instance;
        time.BeforeTick += BeforeTick;
        time.AfterTickLoop += AfterTickLoop;

        var rollback = NetworkRollback.Instance;
        if (RollbackPhysicsSpace) rollback.OnPrepareTick += OnPrepareTick;
        rollback.OnProcessTick += OnProcessTick;
    }

    public override void _ExitTree()
    {
        if (NetworkTime.Instance is { } time)
        {
            time.BeforeTick -= BeforeTick;
            time.AfterTickLoop -= AfterTickLoop;
        }
        if (NetworkRollback.Instance is { } rollback)
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
        if (NetworkRollback.Instance.RollbackFrom == tick)
            RollbackSpace(tick); // First tick of the rollback loop, rewind
        else
            SnapshotSpace(tick); // Subsequent ticks rewrite history
    }

    private void OnProcessTick(int tick) => StepPhysics(NetworkTime.Instance.Ticktime, tick);

    private void AfterTickLoop()
    {
        var historyStart = NetworkRollback.Instance.HistoryStart;
        foreach (var tick in Snapshots.Keys.Where(t => t < historyStart).ToList())
            Snapshots.Remove(tick);
    }

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
}

/// <summary>Physics bodies driven by a PhysicsDriver during rollback.</summary>
public interface INetworkRigidBody
{
    void PhysicsRollbackTick(double delta, int tick);
}
