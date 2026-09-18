using Godot;

namespace CrackNet.Examples.Playground;

/// <summary>
/// A moving platform: a lift or a slider, from PlaygroundPlatform.tscn. An animatable body, so its
/// <see cref="NetworkObject"/> is World: the host moves it and everyone else plays it back. A player standing on it
/// is sent relative to it and drawn on it everywhere; on the player's own peer the copy carries the player through
/// the engine's platform velocity, which an animatable body synced to physics reports and a frozen crate does not.
/// </summary>
public partial class PlaygroundPlatform : AnimatableBody3D
{
    /// <summary>Where it goes and back, from its starting position: up for a lift, sideways for a slider.</summary>
    [Export] public Vector3 Travel { get; set; } = Vector3.Up * 3;

    /// <summary>Seconds for a full trip there and back.</summary>
    [Export] public float Period { get; set; } = 6;

    private Vector3 _start;
    private double _time;

    public override void _Ready() => _start = GlobalPosition;

    public override void _PhysicsProcess(double delta)
    {
        if (!this.Authority.IsLocal) return;   // the others play back what the host sends
        _time += delta;
        GlobalPosition = _start + Travel * (float)(0.5 - 0.5 * Math.Cos(_time / Period * Math.Tau));
    }
}
