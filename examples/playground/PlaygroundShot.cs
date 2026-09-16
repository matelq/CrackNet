using Godot;

namespace Netfox.Examples.Playground;

/// <summary>
/// A slow projectile. It belongs to its shooter: the shooter moves it and decides what it hits, except for a player,
/// whose own peer decides against what it sees - so a dodge on your screen counts.
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

    public override void _Ready()
    {
        // "It hit me", from the player's own peer: the first word wins, the rest find it gone
        Object.EventReceived += (_, _) => Consume();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_consumed) return;

        if (!Object.IsAuthority)
        {
            // The only hit a peer decides for someone else's shot: on its own player, where it sees the shot
            if (Visible && GetTree().GetNodesInGroup("local_player").OfType<PlaygroundPlayer>().FirstOrDefault() is { } me
                && me.GlobalPosition.DistanceTo(GlobalPosition) < HitRadius)
            {
                me.Knock(_velocity.Normalized() * PlayerKnock);
                Object.SendToAuthority(true);
                _consumed = true;
                Hide();
            }
            return;
        }

        GlobalPosition += _velocity * (float)delta;
        _age += delta;

        foreach (var crate in GetTree().GetNodesInGroup("crates").OfType<PlaygroundCrate>())
        {
            if (crate.GlobalPosition.DistanceTo(GlobalPosition) > HitRadius) continue;
            Object.Touch(crate.Object);
            crate.Object.SendToAuthority(_velocity.Normalized() * CrateImpulse);
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
