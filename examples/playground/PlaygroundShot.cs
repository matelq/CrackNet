using Godot;

namespace Netfox.Examples.Playground;

/// <summary>
/// A slow projectile. It belongs to its shooter, whose peer moves it and is the sole arbiter of every hit against the
/// targets it displays. The first target receives knockback and the shot enters its despawn timeline immediately.
/// </summary>
public partial class PlaygroundShot : Node3D
{
    private const float Lifetime = 2.5f, HitRadius = 0.7f, CrateImpulse = 6, PlayerKnock = 6;

    [Synced] public Vector3 NetPosition { get => GlobalPosition; set => GlobalPosition = value; }

    public NetworkObject Object { get; private set; } = null!;
    private Vector3 _velocity;
    private double _age;
    private bool _consumed;

    public static PlaygroundShot Create(Godot.Collections.Dictionary data, int shooter)
    {
        var shot = new PlaygroundShot { Name = data["name"].AsString(), Position = data["origin"].AsVector3(), _velocity = data["velocity"].AsVector3() };
        shot.SetMultiplayerAuthority(shooter);
        shot.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.15f, Height = 0.3f }, MaterialOverride = new StandardMaterial3D { AlbedoColor = Playground.ColorOf(shooter), EmissionEnabled = true, Emission = Playground.ColorOf(shooter) } });
        shot.Object = new NetworkObject { Name = "NetworkObject", Transferable = false, SpreadsAuthority = true };
        shot.AddChild(shot.Object);
        return shot;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_consumed) return;
        if (!Object.IsAuthority) return;

        GlobalPosition += _velocity * (float)delta;
        _age += delta;

        var crateHit = GetTree().GetNodesInGroup("crates").OfType<PlaygroundCrate>()
            .Select(crate => (Crate: crate, Distance: crate.GlobalPosition.DistanceTo(GlobalPosition)))
            .Where(hit => hit.Distance <= HitRadius)
            .MinBy(hit => hit.Distance).Crate;
        var playerHit = GetParent().GetParent().GetNode<Node3D>("Players").GetChildren().OfType<PlaygroundPlayer>()
            .Where(player => player.Peer != Object.Authority && player.Visible)
            .Select(player => (Player: player, Distance: player.GlobalPosition.DistanceTo(GlobalPosition)))
            .Where(hit => hit.Distance <= HitRadius)
            .MinBy(hit => hit.Distance).Player;

        if (crateHit is not null && (playerHit is null
                                     || crateHit.GlobalPosition.DistanceTo(GlobalPosition)
                                     <= playerHit.GlobalPosition.DistanceTo(GlobalPosition)))
        {
            Object.Touch(crateHit.Object);
            crateHit.Object.SendToAuthority(_velocity.Normalized() * CrateImpulse);
            Consume();
            return;
        }
        if (playerHit is not null)
        {
            playerHit.Knock(_velocity.Normalized() * PlayerKnock);
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
