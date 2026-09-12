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
/// <para>
/// Riders are carried by <see cref="PlayerCharacter.RideFloor"/> asking this node for <see cref="MotionAt"/>, rather
/// than by Godot's own moving platform support. See that method for why.
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

    /// <summary>
    /// Where the platform stands on a given tick. From the tick, not from an accumulator: a resimulated tick has to
    /// land in the same place as the first pass did.
    /// </summary>
    public Vector3 PositionAt(int tick)
    {
        var seconds = tick / (double)NetworkTime.Instance.Tickrate;
        return _origin + Travel * (float)Mathf.Sin(seconds / Period * Mathf.Tau);
    }

    /// <summary>
    /// How far it travels over one tick, which is how far a rider has to be moved. A pure function of the tick, so a
    /// rider may ask for it before or after the platform itself has simulated that tick - order does not matter.
    /// </summary>
    public Vector3 MotionAt(int tick) => PositionAt(tick) - PositionAt(tick - 1);

    public void RollbackTick(double delta, int tick, bool isFresh) => Position = PositionAt(tick);
}
