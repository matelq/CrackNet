using Godot;

namespace Netfox.Examples.Playground;

/// <summary>Follows the local player from behind, outside the tick loop: it is a view, not state.</summary>
[GlobalClass]
public partial class PlaygroundCamera : Camera3D
{
    [Export] public Vector3 Offset { get; set; } = new(0, 6, 10);

    private Node3D? _target;

    public void Follow(Node3D target) => _target = target;

    public override void _Process(double delta)
    {
        if (_target is null || !IsInstanceValid(_target)) return;

        // Follows the interpolated position, which is what the TickInterpolator leaves on the node between ticks
        Position = Position.Lerp(_target.Position + Offset, (float)Mathf.Min(1.0, delta * 6.0));
        LookAt(_target.Position);
    }
}
