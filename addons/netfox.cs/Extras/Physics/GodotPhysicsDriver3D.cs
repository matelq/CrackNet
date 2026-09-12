using Godot;
using Array = Godot.Collections.Array;

namespace Netfox.Extras;

/// <summary>
/// Physics driver for Godot builds that expose manual stepping (PhysicsServer.space_step, godotengine/godot PR 76462).
/// Snapshots every PhysicsBody per tick. Port of netfox.extras/physics/godot_driver_3d.gd.
/// </summary>
[GlobalClass]
public partial class GodotPhysicsDriver3D : BodyStatePhysicsDriver
{
    public static bool IsAvailable => PhysicsServer3D.Singleton.HasMethod("space_step");

    private readonly List<PhysicsBody3D> _bodies = new();

    protected override void InitPhysicsSpace()
    {
        if (!IsAvailable)
        {
            Logger.Error("Physics stepping is not available! Is this the right Godot build?");
            return;
        }

        PhysicsSpace = GetViewport().World3D.Space;
        PhysicsServer3D.SpaceSetActive(PhysicsSpace, false);

        GetTree().NodeAdded += NodeAdded;
        ScanTree();
    }

    protected override void PhysicsStep(double delta)
    {
        PhysicsServer3D.Singleton.Call("space_flush_queries", PhysicsSpace);
        PhysicsServer3D.Singleton.Call("space_step", PhysicsSpace, delta);
    }

    protected override void SnapshotSpace(int tick)
    {
        var states = new Dictionary<Rid, Array>();
        foreach (var body in _bodies)
            if (GodotObject.IsInstanceValid(body))
                states[body.GetRid()] = GetBodyStates(body.GetRid());
        Snapshots[tick] = states;
    }

    protected override void RollbackSpace(int tick)
    {
        if (!Snapshots.TryGetValue(tick, out var snapshot)) return;
        foreach (var (rid, state) in snapshot)
            SetBodyStates(rid, state);

        foreach (var body in _bodies)
            if (body is CharacterBody3D or AnimatableBody3D)
                body.ForceUpdateTransform();
    }

    private static Array GetBodyStates(Rid rid) =>
    [
        PhysicsServer3D.BodyGetState(rid, PhysicsServer3D.BodyState.Transform),
        PhysicsServer3D.BodyGetState(rid, PhysicsServer3D.BodyState.LinearVelocity),
        PhysicsServer3D.BodyGetState(rid, PhysicsServer3D.BodyState.AngularVelocity),
        PhysicsServer3D.BodyGetState(rid, PhysicsServer3D.BodyState.Sleeping),
    ];

    private static void SetBodyStates(Rid rid, Array state)
    {
        PhysicsServer3D.BodySetState(rid, PhysicsServer3D.BodyState.Transform, state[0]);
        PhysicsServer3D.BodySetState(rid, PhysicsServer3D.BodyState.LinearVelocity, state[1]);
        PhysicsServer3D.BodySetState(rid, PhysicsServer3D.BodyState.AngularVelocity, state[2]);
        PhysicsServer3D.BodySetState(rid, PhysicsServer3D.BodyState.Sleeping, state[3]);
    }

    private void ScanTree()
    {
        _bodies.Clear();
        foreach (var node in GetTree().Root.FindChildren("*", nameof(PhysicsBody3D), true, false))
            NodeAdded(node);
    }

    private void NodeAdded(Node node)
    {
        if (node is not PhysicsBody3D body) return;
        _bodies.Add(body);
        body.TreeExiting += () => NodeRemoved(body);
    }

    private void NodeRemoved(PhysicsBody3D body)
    {
        _bodies.Remove(body);
        var rid = body.GetRid();
        foreach (var snapshot in Snapshots.Values)
            ((System.Collections.Generic.Dictionary<Rid, Array>)snapshot).Remove(rid);
    }
}
