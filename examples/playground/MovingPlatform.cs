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

    /// <summary>Riders look platforms up by this rather than by casting a ray. See <see cref="CarriesAt"/>.</summary>
    public const string Group = "moving_platforms";

    private Vector3 _origin;
    private Vector3 _halfExtents;

    public override void _Ready()
    {
        _origin = Position;
        SyncToPhysics = false;
        AddToGroup(Group);

        // By type rather than by name: AddChild without an explicit name gives a node something like
        // "@CollisionShape3D@2", so a platform built in code would never be found by the name the scene uses - and
        // the failure is quiet, leaving the carry box at zero size.
        var shape = GetChildren().OfType<CollisionShape3D>().FirstOrDefault()?.Shape as BoxShape3D;
        if (shape is null) GD.PushWarning($"{Name}: no BoxShape3D child, nothing will be carried");
        _halfExtents = (shape?.Size ?? Vector3.Zero) / 2;
    }

    /// <summary>
    /// Whether a point is standing on this platform on a given tick - the test a rider makes before asking to be
    /// carried.
    /// <para>
    /// Deliberately arithmetic rather than a physics query. A query answers from the physics space, which holds the
    /// transforms of the last physics step and not of the tick being resimulated, so during a rewind it can say "on
    /// the platform" where the first pass said "off it" - and a rider that is carried on one pass and not on another
    /// walks away from itself by a tick of platform travel each time. This is a function of the point and the tick,
    /// which is what a rollback tick is allowed to depend on.
    /// </para>
    /// </summary>
    public bool CarriesAt(Vector3 point, int tick)
    {
        // PositionAt is relative to the parent, which is where this node's own Position lives
        var parent = (GetParent() as Node3D)?.GlobalPosition ?? Vector3.Zero;
        var centre = parent + PositionAt(tick);
        var above = point.Y - centre.Y;

        // Standing on the top face rather than passing by underneath it. One player height of headroom is plenty:
        // a rider's origin sits half its capsule above the surface.
        return above > 0 && above < 2.0f
            && Mathf.Abs(point.X - centre.X) <= _halfExtents.X + 0.5f
            && Mathf.Abs(point.Z - centre.Z) <= _halfExtents.Z + 0.5f;
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
