// Built in code: CRN006 has nothing to check here
#pragma warning disable CRN006

using Godot;

namespace CrackNet.Tests;

/// <summary>A moving platform as the playground's: an animatable body the host moves along <see cref="Velocity"/> every physics step.</summary>
public partial class Lift : AnimatableBody3D
{
    public Vector3 Velocity { get; set; }

    public override void _PhysicsProcess(double delta)
    {
        if (IsMultiplayerAuthority()) GlobalPosition += Velocity * (float)delta;
    }
}
