// Built in code, not from a scene: CRN006 has nothing to check here
#pragma warning disable CRN006

using Godot;

namespace CrackNet.Tests;

/// <summary>A character body that walks at a constant velocity on its own peer.</summary>
public partial class Walker : CharacterBody3D, IAttachmentChanged
{
    public Vector3 Walk { get; set; }

    /// <summary>Keeps its vertical velocity under gravity, as a game's player does, instead of setting it every frame.</summary>
    public bool Falls { get; set; }

    /// <summary>The one-shot pattern: a counter bumped in the tick of the action, so an observer plays the animation at the action's display tick.</summary>
    [Synced] public int Gestures { get; set; }

    /// <summary>How many times the library told this walker that what hangs on it changed, on this peer.</summary>
    public int AttachmentChanges { get; private set; }

    public void OnAttachmentChanged() => AttachmentChanges++;

    public override void _PhysicsProcess(double delta)
    {
        // Carried: the library places it, as a game's controller skips its movement while AttachedTo is set
        if (!IsMultiplayerAuthority() || this.AttachedTo is not null) return;
        Velocity = (Falls ? Walk + new Vector3(0, Velocity.Y - 14 * (float)delta, 0) : Walk + Vector3.Down) + this.ImpulseVelocity;
        MoveAndSlide();
    }
}
