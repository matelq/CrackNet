using Godot;
using Array = Godot.Collections.Array;

namespace Netfox.Extras;

/// <summary>2D counterpart of GodotPhysicsDriver3D. Port of netfox.extras/physics/godot_driver_2d.gd.</summary>
[GlobalClass]
public partial class GodotPhysicsDriver2D : BodyStatePhysicsDriver
{
    public static bool IsAvailable => PhysicsServer2D.Singleton.HasMethod("space_step");

    private readonly List<PhysicsBody2D> _bodies = new();

    protected override void InitPhysicsSpace()
    {
        if (!IsAvailable)
        {
            Logger.Error("Physics stepping is not available! Is this the right Godot build?");
            return;
        }

        PhysicsSpace = GetViewport().World2D.Space;
        PhysicsServer2D.SpaceSetActive(PhysicsSpace, false);

        GetTree().NodeAdded += NodeAdded;
        ScanTree();
    }

    protected override void PhysicsStep(double delta)
    {
        PhysicsServer2D.Singleton.Call("space_flush_queries", PhysicsSpace);
        PhysicsServer2D.Singleton.Call("space_step", PhysicsSpace, delta);
    }

    protected override void SnapshotSpace(int tick)
    {
        var states = new Dictionary<Rid, Array>();
        foreach (var body in _bodies)
        {
            if (!GodotObject.IsInstanceValid(body)) continue;
            if (body is CharacterBody2D) body.ForceUpdateTransform();
            states[body.GetRid()] = GetBodyStates(body.GetRid());
        }
        Snapshots[tick] = states;
    }

    protected override void RollbackSpace(int tick)
    {
        if (!Snapshots.TryGetValue(tick, out var snapshot)) return;
        foreach (var (rid, state) in snapshot)
            SetBodyStates(rid, state);

        foreach (var body in _bodies)
            if (body is CharacterBody2D or AnimatableBody2D)
                body.ForceUpdateTransform();
    }

    private static Array GetBodyStates(Rid rid) =>
    [
        PhysicsServer2D.BodyGetState(rid, PhysicsServer2D.BodyState.Transform),
        PhysicsServer2D.BodyGetState(rid, PhysicsServer2D.BodyState.LinearVelocity),
        PhysicsServer2D.BodyGetState(rid, PhysicsServer2D.BodyState.AngularVelocity),
        PhysicsServer2D.BodyGetState(rid, PhysicsServer2D.BodyState.Sleeping),
    ];

    private static void SetBodyStates(Rid rid, Array state)
    {
        PhysicsServer2D.BodySetState(rid, PhysicsServer2D.BodyState.Transform, state[0]);
        PhysicsServer2D.BodySetState(rid, PhysicsServer2D.BodyState.LinearVelocity, state[1]);
        PhysicsServer2D.BodySetState(rid, PhysicsServer2D.BodyState.AngularVelocity, state[2]);
        PhysicsServer2D.BodySetState(rid, PhysicsServer2D.BodyState.Sleeping, state[3]);
    }

    private void ScanTree()
    {
        _bodies.Clear();
        foreach (var node in GetTree().Root.FindChildren("*", nameof(PhysicsBody2D), true, false))
            NodeAdded(node);
    }

    private void NodeAdded(Node node)
    {
        if (node is not PhysicsBody2D body) return;
        _bodies.Add(body);
        body.TreeExiting += () => NodeRemoved(body);
    }

    private void NodeRemoved(PhysicsBody2D body)
    {
        _bodies.Remove(body);
        var rid = body.GetRid();
        foreach (var snapshot in Snapshots.Values)
            ((System.Collections.Generic.Dictionary<Rid, Array>)snapshot).Remove(rid);
    }
}
