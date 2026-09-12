using Godot;

namespace Netfox.Examples.Playground;

/// <summary>
/// A platform sliding back and forth, there to make misprediction visible. It has no input of its own, so it is
/// simulated on every peer rather than replicated: its position is a function of the tick and nothing else.
/// <para>
/// A player standing on it is what makes a wrong prediction obvious. If the platform were driven by the frame clock
/// instead of the tick, a resimulation would put it somewhere else than the first pass did, and anyone riding it would
/// be dragged off - which is the failure this sample exists to let you see.
/// </para>
/// </summary>
[GlobalClass]
public partial class MovingPlatform : AnimatableBody3D, IRollbackTick
{
    [Export] public Vector3 Travel { get; set; } = new(6, 0, 0);

    /// <summary>Seconds for one full there-and-back.</summary>
    [Export] public float Period { get; set; } = 6.0f;

    private Vector3 _origin;

    public override void _Ready()
    {
        _origin = Position;
        SyncToPhysics = false;
    }

    public void RollbackTick(double delta, int tick, bool isFresh)
    {
        // From the tick, not from an accumulator: a resimulated tick has to land in the same place as the first pass
        var seconds = tick / (double)NetworkTime.Instance.Tickrate;
        var phase = Mathf.Sin(seconds / Period * Mathf.Tau);
        Position = _origin + Travel * (float)phase;
    }
}
