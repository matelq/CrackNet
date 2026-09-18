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

    /// <summary>The walk blend a game feeds its AnimationTree: 0 standing, 1 at full speed, set on the authority every physics frame.</summary>
    [Synced] public float WalkBlend { get; set; }

    /// <summary>
    /// The one-shot pattern: a counter bumped in the tick of the action, so an observer plays the animation at the
    /// action's display tick. The setter plays the difference each sample brings, once the first value is known: two
    /// bumps inside one snapshot arrive as one step of two, and a late joiner's first value is history.
    /// </summary>
    [Synced]
    public int Gestures
    {
        get;
        set
        {
            if (_gesturesKnown) GesturesPlayed += value - field;
            _gesturesKnown = true;
            field = value;
        }
    }

    /// <summary>How many gestures this peer played from <see cref="Gestures"/>.</summary>
    public int GesturesPlayed { get; private set; }

    private bool _gesturesKnown;

    /// <summary>Root motion: the animation's displacement moves the body, read from this mixer each physics frame.</summary>
    public AnimationMixer? RootMotion { get; set; }

    public override void _Ready() => _gesturesKnown = IsMultiplayerAuthority();   // its own count starts at zero

    /// <summary>How many times the library told this walker that what hangs on it changed, on this peer.</summary>
    public int AttachmentChanges { get; private set; }

    public void OnAttachmentChanged() => AttachmentChanges++;

    public override void _PhysicsProcess(double delta)
    {
        // Carried: the library places it, as a game's controller skips its movement while AttachedTo is set
        if (this.AttachedTo is not null) return;
        if (!IsMultiplayerAuthority()) return;
        if (RootMotion is { } mixer)
        {
            // Root motion belongs on the authority: its result travels as the transform
            Velocity = mixer.GetRootMotionPosition() / (float)delta + Vector3.Down;
            MoveAndSlide();
            return;
        }
        Velocity = (Falls ? Walk + new Vector3(0, Velocity.Y - 14 * (float)delta, 0) : Walk + Vector3.Down) + this.ImpulseVelocity;
        MoveAndSlide();
        WalkBlend = Mathf.Clamp(new Vector2(Velocity.X, Velocity.Z).Length() / 2, 0, 1);
    }
}
