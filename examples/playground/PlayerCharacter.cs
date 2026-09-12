using Godot;

namespace Netfox.Examples.Playground;

/// <summary>
/// A player driven entirely from <see cref="PlayerInput"/>, so every peer can arrive at the same result for a tick.
/// <para>
/// Movement lives in <see cref="RollbackTick"/> rather than in <c>_PhysicsProcess</c>: netfox calls it once per
/// simulated tick, and again for every tick a resimulation covers. That is the whole contract - the method has to be a
/// function of the state it is given and the input for that tick, and of nothing else. Reading the wall clock, a
/// random number or an unreplicated field here is what makes peers disagree.
/// </para>
/// </summary>
[GlobalClass]
public partial class PlayerCharacter : CharacterBody3D, IRollbackTick
{
    [Export] public float Speed { get; set; } = 5.0f;
    [Export] public float JumpVelocity { get; set; } = 5.0f;
    [Export] public int MaxJumps { get; set; } = 2;

    /// <summary>
    /// Jumps left before touching the ground again. Its own state, so it carries a <c>[RollbackState]</c> attribute
    /// and the synchronizer gathers the path from the property itself.
    /// <para>
    /// <c>position</c> and <c>velocity</c> are rollback state too, but they belong to CharacterBody3D rather than to
    /// this class, so there is nowhere to put an attribute: those two are listed as strings on the synchronizer in the
    /// scene. The synchronizer takes both sources.
    /// </para>
    /// </summary>
    [RollbackState] public int JumpsLeft { get; set; }

    /// <summary>
    /// Whether jump was held last tick. Edge detection needs the previous input, and during a resimulation "previous"
    /// means the tick being resimulated, not the latest one - so it has to be rollback state like anything else.
    /// </summary>
    [RollbackState] public bool JumpHeld { get; set; }

    /// <summary>True on the peer that owns this player's input, which is the one the camera follows.</summary>
    public bool IsLocal => Input.IsMultiplayerAuthority();

    public PlayerInput Input { get; private set; } = null!;
    public RollbackSynchronizer Synchronizer { get; private set; } = null!;

    private float _gravity;

    public override void _Ready()
    {
        Input = GetNode<PlayerInput>("Input");
        Synchronizer = GetNode<RollbackSynchronizer>("RollbackSynchronizer");
        _gravity = (float)(double)ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8);
        JumpsLeft = MaxJumps;
    }

    public void RollbackTick(double delta, int tick, bool isFresh)
    {
        var velocity = Velocity;

        if (IsOnFloor())
        {
            JumpsLeft = MaxJumps;
            velocity.Y = Mathf.Max(velocity.Y, 0);
        }
        else
        {
            velocity.Y -= _gravity * (float)delta;
        }

        var justPressed = Input.Jump && !JumpHeld;
        JumpHeld = Input.Jump;

        if (justPressed && JumpsLeft > 0)
        {
            velocity.Y = JumpVelocity;
            JumpsLeft--;
        }

        var direction = new Vector3(Input.Movement.X, 0, Input.Movement.Y);
        velocity.X = direction.X * Speed;
        velocity.Z = direction.Z * Speed;

        // MoveAndSlide uses the physics frame delta, which is not the tick delta a resimulation runs at. Scaling the
        // velocity around the call is how netfox's own examples reconcile the two.
        var scale = (float)(delta / GetPhysicsProcessDeltaTime());
        Velocity = velocity * scale;
        MoveAndSlide();
        Velocity = Velocity / scale;
    }
}
