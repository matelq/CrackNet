using Godot;

namespace Netfox.Tests;

/// <summary>A test projectile whose authority alone selects and notifies the first displayed target it reaches.</summary>
public partial class HarnessProjectile : Node3D
{
    private const float Speed = 10;
    private const float HitRadius = 0.55f;

    [Synced] public Vector3 Location { get; set; }

    private HarnessBody[] _targets = [];
    private NetworkObject _object = null!;

    public static HarnessProjectile Spawn(Node stack, string name, int authority, params HarnessBody[] targets)
    {
        var shot = new HarnessProjectile { Name = name, _targets = targets };
        shot.SetMultiplayerAuthority(authority);
        shot._object = new NetworkObject { Name = "NetworkObject", Transferable = false };
        shot.AddChild(shot._object);
        stack.AddChild(shot);
        return shot;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!_object.IsAuthority || _object.DespawnRequested) return;
        Location += Vector3.Right * Speed * (float)delta;

        var target = _targets.Where(candidate => candidate.Visible)
            .Select(candidate => (Body: candidate, Distance: candidate.Location.DistanceTo(Location)))
            .Where(candidate => candidate.Distance <= HitRadius)
            .MinBy(candidate => candidate.Distance).Body;
        if (target is null) return;

        target.Object.SendToAuthority(true);
        _object.Despawn();
    }
}
