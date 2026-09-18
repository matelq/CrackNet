using Godot;

namespace CrackNet.Examples.Playground;

/// <summary>
/// A crate anyone can push, grab and throw, from PlaygroundCrate.tscn: a rigid body, a box shape, a box mesh and a
/// <see cref="NetworkObject"/>. That node does the networking: the crate is a rigid body, so it is Shared, frozen
/// wherever another peer simulates it, passes authority to what it hits and goes back to the host at rest. All this
/// class adds is a tint in the colour of the peer simulating it right now.
/// </summary>
public partial class PlaygroundCrate : RigidBody3D, IAuthorityChanged
{
    private StandardMaterial3D _material = null!;

    public override void _Ready()
    {
        var mesh = GetNode<MeshInstance3D>("MeshInstance3D");
        // Its own copy: the scene's material is shared by every crate, and each shows its own peer's colour
        _material = (StandardMaterial3D)mesh.MaterialOverride.Duplicate();
        mesh.MaterialOverride = _material;

        Playground.SlotsChanged += Refresh;
        Refresh();
    }

    public override void _ExitTree() => Playground.SlotsChanged -= Refresh;

    /// <summary>The library calls this on every peer once the crate has changed hands.</summary>
    public void OnAuthorityChanged() => Refresh();

    private void Refresh()
    {
        _material.AlbedoColor = Playground.ColorOf(this.Authority.Peer).Lerp(Colors.SaddleBrown, 0.35f);
        // A held crate is moved by hand, a teleport every frame: colliding, it would land inside the crates around it
        // and the physics engine would blow them out of the world. It passes through things while held, on every peer
        CollisionLayer = this.ClaimedBy != 0 ? 0u : 1u;
        CollisionMask = this.ClaimedBy != 0 ? 0u : 1u;
        if (IsInsideTree()) PlaytestLog.Authority(this);
    }
}
