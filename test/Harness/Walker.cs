using Godot;

namespace Netfox.Tests;

/// <summary>A character body that walks at a constant velocity on its own peer.</summary>
public partial class Walker : CharacterBody3D
{
    public Vector3 Walk { get; set; }

    public override void _PhysicsProcess(double delta)
    {
        if (!IsMultiplayerAuthority()) return;
        Velocity = Walk + Vector3.Down;
        MoveAndSlide();
    }
}
