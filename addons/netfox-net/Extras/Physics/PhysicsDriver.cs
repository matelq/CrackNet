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
        if (RollbackPhysicsSpace)
        {
            rollback.OnPrepareTick += OnPrepareTick;
            rollback.AfterPrepareTick += AfterPrepareTick;
        }
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
            rollback.AfterPrepareTick -= AfterPrepareTick;
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

    /// <summary>
    /// Rolling the space back also moves every kinematic body in it to where the snapshot had it - but their nodes
    /// are restored by netfox from its own history, a moment later, and a node only pushes its transform to the body
    /// when the value changes. A player standing still against another was left with its body where the snapshot
    /// put it and its node where history did, and the other player walked into the node. So on every resimulated
    /// tick, once history has been restored, every body that is not rolled back by state of its own is told where
    /// its node is - and the space is flushed once, so the tick's queries see all of them.
    /// <para>
    /// Once, here, and not after each body moves: a flush inside the tick makes the second body to move see the
    /// first one's new position, and which body moves first is the scene tree's business and differs between peers.
    /// Two peers that simulate the same tick from the same state then disagree by 14cm for good (seen in CI). Every
    /// body testing against where everything was at the start of the tick is what keeps the tick a function of its
    /// state.
    /// </para>
    /// </summary>
    private void AfterPrepareTick(int tick)
    {
        PushNodeTransforms(GetTree().Root);
        FlushQueries();
    }

    // ponytail: a tree walk per resimulated tick; a group of kinematic bodies if the tree ever gets big
    protected virtual void PushNodeTransforms(Node node)
    {
        if (node is PhysicsBody3D body3D and not RigidBody3D)
            PhysicsServer3D.BodySetState(body3D.GetRid(), PhysicsServer3D.BodyState.Transform, body3D.GlobalTransform);
        else if (node is PhysicsBody2D body2D and not RigidBody2D)
            PhysicsServer2D.BodySetState(body2D.GetRid(), PhysicsServer2D.BodyState.Transform, body2D.GlobalTransform);
        foreach (var child in node.GetChildren()) PushNodeTransforms(child);
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

    /// <summary>
    /// Makes every transform written so far visible to queries. Rapier applies a body's new transform on the next
    /// step, and <c>MoveAndSlide</c> is a query: without this, the transforms pushed above are not what the tick
    /// tests against (two players walking into each other passed to 3cm apart instead of stopping at 80). Nothing
    /// to do on an engine that applies transforms as they are written.
    /// </summary>
    protected virtual void FlushQueries() { }

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
