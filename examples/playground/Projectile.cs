using Godot;

namespace Netfox.Examples.Playground;

/// <summary>
/// A shot in flight. Deliberately not rollback state: the weapon toolkit spawns one on the shooter immediately and
/// the authority confirms or declines it, which is the cheap answer to "I want shooting" - full rollback of
/// projectiles is the expensive one, and netfox does not do it.
/// <para>
/// So it moves on the network tick rather than in a rollback tick, and every peer runs the same arithmetic from the
/// transform it was given. Nothing here is replicated after the spawn.
/// </para>
/// </summary>
[GlobalClass]
public partial class Projectile : Node3D
{
    [Export] public float Speed { get; set; } = 24.0f;

    /// <summary>Ticks before it gives up, so a missed shot does not live forever.</summary>
    [Export] public int Lifetime { get; set; } = 60;

    private int _ticksLived;

    public override void _Ready() => NetworkTime.Instance.AfterTick += Advance;

    public override void _ExitTree()
    {
        if (NetworkTime.Instance is { } time) time.AfterTick -= Advance;
    }

    private void Advance(double delta, int tick)
    {
        GlobalPosition += -GlobalTransform.Basis.Z * Speed * (float)delta;

        if (++_ticksLived < Lifetime) return;
        NetworkTime.Instance.AfterTick -= Advance;
        QueueFree();
    }
}
