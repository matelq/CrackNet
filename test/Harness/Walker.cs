using Godot;

namespace Netfox.Tests;

/// <summary>A character body that walks at a constant velocity on its own peer.</summary>
public partial class Walker : CharacterBody3D
{
    public Vector3 Walk { get; set; }

    /// <summary>Keeps its vertical velocity under gravity, as a game's player does, instead of setting it every frame.</summary>
    public bool Falls { get; set; }

    public override void _PhysicsProcess(double delta)
    {
        if (!IsMultiplayerAuthority()) return;
        Velocity = Falls ? Walk + new Vector3(0, Velocity.Y - 14 * (float)delta, 0) : Walk + Vector3.Down;
        MoveAndSlide();
    }
}
