using Godot;

namespace Netfox.Examples.Playground;

/// <summary>
/// A slow projectile. It belongs to its shooter, whose peer moves it and is the sole arbiter of every hit against the
/// targets it displays. The first target receives knockback and the shot enters its despawn timeline immediately.
/// </summary>
public partial class PlaygroundShot : Node3D
{
    private const float Lifetime = 2.5f, HitRadius = 0.7f, CrateImpulse = 6, PlayerKnock = 6;

    /// <summary>Half the crate's size plus the shot's radius: a crate is a box, not a sphere around its centre.</summary>
    private const float CrateHalfExtent = 0.5f + 0.15f;

    private bool IsInside(Vector3 centre, float halfExtent)
    {
        var offset = GlobalPosition - centre;
        return Math.Abs(offset.X) <= halfExtent && Math.Abs(offset.Y) <= halfExtent && Math.Abs(offset.Z) <= halfExtent;
    }

    public NetworkObject Object { get; private set; } = null!;
    private Vector3 _velocity;
    private double _age;
    private bool _consumed;

    public static PlaygroundShot Create(Godot.Collections.Dictionary data, int shooter)
    {
        var shot = new PlaygroundShot { Name = data["name"].AsString(), Position = data["origin"].AsVector3(), _velocity = data["velocity"].AsVector3() };
        shot.SetMultiplayerAuthority(shooter);
        shot.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.15f, Height = 0.3f }, MaterialOverride = new StandardMaterial3D { AlbedoColor = Playground.ColorOf(shooter), EmissionEnabled = true, Emission = Playground.ColorOf(shooter) } });
        shot.Object = new NetworkObject { Name = "NetworkObject" };
        shot.AddChild(shot.Object);
        return shot;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_consumed) return;
        if (!Object.IsAuthority) return;

        GlobalPosition += _velocity * (float)delta;
        _age += delta;

        // Nearest first, and nothing when nothing is in reach: MinBy over a value tuple throws on an empty sequence,
        // which it did every frame of every flight, so no shot ever hit anything
        var crateHit = GetTree().GetNodesInGroup("crates").OfType<PlaygroundCrate>()
            .Where(crate => IsInside(crate.GlobalPosition, CrateHalfExtent))
            .OrderBy(crate => crate.GlobalPosition.DistanceTo(GlobalPosition))
            .FirstOrDefault();
        var playerHit = GetParent().GetParent().GetNode<Node3D>("Players").GetChildren().OfType<PlaygroundPlayer>()
            .Where(player => player.Peer != Object.Authority && player.Visible)
            .Where(player => player.GlobalPosition.DistanceTo(GlobalPosition) <= HitRadius)
            .OrderBy(player => player.GlobalPosition.DistanceTo(GlobalPosition))
            .FirstOrDefault();

        if (crateHit is not null && (playerHit is null
                                     || crateHit.GlobalPosition.DistanceTo(GlobalPosition)
                                     <= playerHit.GlobalPosition.DistanceTo(GlobalPosition)))
        {
            Object.Touch(crateHit.Object);
            crateHit.Object.Knock(_velocity.Normalized() * CrateImpulse);
            Consume();
            return;
        }
        if (playerHit is not null)
        {
            playerHit.Object.Knock(_velocity.Normalized() * PlayerKnock);
            Consume();
            return;
        }

        if (_age > Lifetime) Consume();
    }

    private void Consume()
    {
        _consumed = true;
        if (Object.IsAuthority) Object.Despawn();
    }
}
