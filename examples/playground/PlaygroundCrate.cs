using Godot;

namespace Netfox.Examples.Playground;

/// <summary>
/// A crate anyone can push, grab and throw. Its <see cref="NetworkObject"/> does the networking: the crate is a rigid
/// body, so it is Shared, frozen wherever another peer simulates it, passes authority to what it hits and goes back to
/// the host at rest. All this class adds is a tint in the colour of the peer simulating it right now.
/// </summary>
public partial class PlaygroundCrate : RigidBody3D
{
    public NetworkObject Object { get; private set; } = null!;
    private StandardMaterial3D _material = null!;

    public static PlaygroundCrate Create(string name, Vector3 position)
    {
        var crate = new PlaygroundCrate { Name = name, Position = position, Mass = 2 };
        crate.SetMultiplayerAuthority(1);
        crate.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One } });
        crate._material = new StandardMaterial3D();
        crate.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = Vector3.One }, MaterialOverride = crate._material });
        crate.Object = new NetworkObject { Name = "NetworkObject" };
        crate.AddChild(crate.Object);
        crate.AddToGroup("crates");
        return crate;
    }

    public override void _Ready()
    {
        Object.AuthorityChanged += Refresh;
        Playground.SlotsChanged += Refresh;
        Refresh();
    }

    public override void _ExitTree() => Playground.SlotsChanged -= Refresh;

    private void Refresh()
    {
        _material.AlbedoColor = Playground.ColorOf(Object.Authority.Peer).Lerp(Colors.SaddleBrown, 0.35f);
        // A held crate is moved by hand, a teleport every frame: colliding, it would land inside the crates around it
        // and the physics engine would blow them out of the world. It passes through things while held, on every peer
        CollisionLayer = Object.Holder != 0 ? 0u : 1u;
        CollisionMask = Object.Holder != 0 ? 0u : 1u;
    }
}
