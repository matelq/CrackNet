using Godot;
using Array = Godot.Collections.Array;

namespace Netfox.Extras;

/// <summary>RigidBody2D exposing its physics state as one synchronizable property. Port of netfox.extras/physics/network-rigid-body-2d.gd.</summary>
[GlobalClass]
[Icon("res://addons/netfox.cs/icons/network-rigid-body-2d.svg")]
public partial class NetworkRigidBody2D : RigidBody2D, INetworkRigidBody
{
    private PhysicsDirectBodyState2D? _directState;

    private PhysicsDirectBodyState2D DirectState => _directState ??= PhysicsServer2D.BodyGetDirectState(GetRid())!;

    /// <summary>[origin, rotation, linear velocity, angular velocity, sleeping]</summary>
    public Array PhysicsState
    {
        get => GetState();
        set => SetState(value);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationReady) AddToGroup(PhysicsDriver.NetworkRigidBodyGroup);
    }

    public Array GetState()
    {
        var state = DirectState;
        return [state.Transform.Origin, state.Transform.Rotation, state.LinearVelocity, state.AngularVelocity, state.Sleeping];
    }

    public void SetState(Array remoteState)
    {
        var state = DirectState;
        state.Transform = new Transform2D(remoteState[1].AsSingle(), remoteState[0].AsVector2());
        state.LinearVelocity = remoteState[2].AsVector2();
        state.AngularVelocity = remoteState[3].AsSingle();
        state.Sleeping = remoteState[4].AsBool();
    }

    public virtual void PhysicsRollbackTick(double delta, int tick) { }
}

/// <summary>RigidBody3D exposing its physics state as one synchronizable property. Port of netfox.extras/physics/network-rigid-body-3d.gd.</summary>
[GlobalClass]
[Icon("res://addons/netfox.cs/icons/network-rigid-body-3d.svg")]
public partial class NetworkRigidBody3D : RigidBody3D, INetworkRigidBody
{
    private PhysicsDirectBodyState3D? _directState;

    private PhysicsDirectBodyState3D DirectState => _directState ??= PhysicsServer3D.BodyGetDirectState(GetRid())!;

    /// <summary>[origin, rotation quaternion, linear velocity, angular velocity, sleeping]</summary>
    public Array PhysicsState
    {
        get => GetState();
        set => SetState(value);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationReady) AddToGroup(PhysicsDriver.NetworkRigidBodyGroup);
    }

    public Array GetState()
    {
        var state = DirectState;
        return [state.Transform.Origin, state.Transform.Basis.GetRotationQuaternion(), state.LinearVelocity, state.AngularVelocity, state.Sleeping];
    }

    public void SetState(Array remoteState)
    {
        var state = DirectState;
        state.Transform = new Transform3D(new Basis(remoteState[1].AsQuaternion()), remoteState[0].AsVector3());
        state.LinearVelocity = remoteState[2].AsVector3();
        state.AngularVelocity = remoteState[3].AsVector3();
        state.Sleeping = remoteState[4].AsBool();
    }

    public virtual void PhysicsRollbackTick(double delta, int tick) { }
}
