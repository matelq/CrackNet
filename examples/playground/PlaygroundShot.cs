using Godot;

namespace Netfox.Examples.Playground;

/// <summary>
/// A slow projectile, spawned by its shooter with <c>PlaygroundShot.Spawn(at, velocity)</c> from PlaygroundShot.tscn.
/// It belongs to its shooter, whose peer moves it and is the sole arbiter of every hit against the targets it displays. The first
/// target receives knockback and the shot enters its despawn timeline immediately.
/// </summary>
public partial class PlaygroundShot : Node3D, ISpawnedWith<Vector3>
{
    private const float Lifetime = 2.5f, HitRadius = 0.7f, CrateImpulse = 6, PlayerKnock = 6;

    /// <summary>Half the crate's size plus the shot's radius: a crate is a box, not a sphere around its centre.</summary>
    private const float CrateHalfExtent = 0.5f + 0.15f;

    private bool IsInside(Vector3 centre, float halfExtent)
    {
        var offset = GlobalPosition - centre;
        return Math.Abs(offset.X) <= halfExtent && Math.Abs(offset.Y) <= halfExtent && Math.Abs(offset.Z) <= halfExtent;
    }

    /// <summary>Spawn data; only the shooter's peer moves the shot.</summary>
    public Vector3 Velocity { get; private set; }

    public void OnSpawned(Vector3 velocity) => Velocity = velocity;

    /// <summary>The smoke prints what every shot did, so a missed check says why.</summary>
    public static bool Diagnose { get; set; }
    private double _age;
    private bool _consumed;

    public override void _EnterTree()
    {
        var color = Playground.ColorOf(GetMultiplayerAuthority());
        GetNode<MeshInstance3D>("Body").MaterialOverride = new StandardMaterial3D { AlbedoColor = color, EmissionEnabled = true, Emission = color };
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_consumed) return;
        if (!this.Authority.IsLocal) return;

        GlobalPosition += Velocity * (float)delta;
        _age += delta;

        // Nearest first, and nothing when nothing is in reach: MinBy over a value tuple throws on an empty sequence,
        // which it did every frame of every flight, so no shot ever hit anything
        var crateHit = GetTree().GetNodesInGroup("crates").OfType<PlaygroundCrate>()
            .Where(crate => IsInside(crate.GlobalPosition, CrateHalfExtent))
            .OrderBy(crate => crate.GlobalPosition.DistanceTo(GlobalPosition))
            .FirstOrDefault();
        var playerHit = GetParent().GetParent().GetNode<Node3D>("Players").GetChildren().OfType<PlaygroundPlayer>()
            .Where(player => player.Peer != this.Authority.Peer && player.Visible)
            .Where(player => player.GlobalPosition.DistanceTo(GlobalPosition) <= HitRadius)
            .OrderBy(player => player.GlobalPosition.DistanceTo(GlobalPosition))
            .FirstOrDefault();

        if (crateHit is not null && (playerHit is null
                                     || crateHit.GlobalPosition.DistanceTo(GlobalPosition)
                                     <= playerHit.GlobalPosition.DistanceTo(GlobalPosition)))
        {
            if (Diagnose) GD.Print($"SHOT {Name} hit {crateHit.Name} (authority {crateHit.Net().Authority.Peer}) at {GlobalPosition}");
            PlaytestLog.Note(this, $"SHOT {Name} hit {crateHit.Name} (authority {crateHit.Net().Authority.Peer}) at {GlobalPosition:F2}");
            this.Push(crateHit, Velocity.Normalized() * CrateImpulse);
            Consume();
            return;
        }
        if (playerHit is not null)
        {
            if (Diagnose) GD.Print($"SHOT {Name} hit {playerHit.Name} at {GlobalPosition}");
            this.Push(playerHit, Velocity.Normalized() * PlayerKnock);
            Consume();
            return;
        }

        if (_age <= Lifetime) return;
        if (Diagnose) GD.Print($"SHOT {Name} expired at {GlobalPosition}");
        Consume();
    }

    private void Consume()
    {
        _consumed = true;
        if (this.Authority.IsLocal) this.Despawn();
    }
}
