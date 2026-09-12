using Godot;
using Netfox.Extras;

namespace Netfox.Examples.Playground;

/// <summary>
/// A player driven entirely from <see cref="PlayerInput"/>, so every peer can arrive at the same result for a tick.
/// <para>
/// The movement itself lives in the states under the <see cref="RewindableStateMachine"/>; this class holds the
/// shared state and the pieces both states need. What matters either way is the contract: a tick has to be a function
/// of the state it is given and the input for that tick, and of nothing else. Reading the wall clock, a random number
/// or an unreplicated field is what makes peers disagree.
/// </para>
/// </summary>
[GlobalClass]
public partial class PlayerCharacter : CharacterBody3D
{
    [Export] public float Speed { get; set; } = 5.0f;
    [Export] public float JumpVelocity { get; set; } = 5.0f;
    [Export] public int MaxJumps { get; set; } = 2;

    /// <summary>
    /// Jumps left before touching the ground again. Its own property, so it carries a <c>[RollbackState]</c>
    /// attribute and the synchronizer gathers the path from the property itself.
    /// <para>
    /// <c>position</c> and <c>velocity</c> are rollback state too, but they belong to CharacterBody3D rather than to
    /// this class, so there is nowhere to put an attribute: those two are listed as strings on the synchronizer in
    /// the scene. The synchronizer takes both sources.
    /// </para>
    /// </summary>
    [RollbackState] public int JumpsLeft { get; set; }

    /// <summary>
    /// Whether jump was held last tick. Edge detection needs the previous input, and during a resimulation
    /// "previous" means the tick being resimulated, not the latest one - so it has to be rollback state too.
    /// </summary>
    [RollbackState] public bool JumpHeld { get; set; }

    /// <summary>True on the peer that owns this player's input, which is the one the camera follows.</summary>
    public bool IsLocal => Input.IsMultiplayerAuthority();

    public PlayerInput Input { get; private set; } = null!;
    public PlayerWeapon Weapon { get; private set; } = null!;
    public RollbackSynchronizer Synchronizer { get; private set; } = null!;
    public RewindableStateMachine StateMachine { get; private set; } = null!;

    public float Gravity { get; private set; }

    public override void _Ready()
    {
        Input = GetNode<PlayerInput>("Input");
        Synchronizer = GetNode<RollbackSynchronizer>("RollbackSynchronizer");
        StateMachine = GetNode<RewindableStateMachine>("RewindableStateMachine");
        Weapon = GetNode<PlayerWeapon>("Weapon");
        Gravity = (float)(double)ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8);
        JumpsLeft = MaxJumps;

        ApplySchema();

        // Set here rather than in the scene: the machine collects its states as children are added, which happens
        // after a scene sets the node's own properties, so a State written into the .tscn would find nothing.
        // Deferred, because whether the machine has collected its children yet depends on notification order, and
        // getting it wrong is quiet: the machine warns and stays in no state at all, so nothing ever simulates.
        Callable.From(() =>
        {
            StateMachine.UpdateStates();
            StateMachine.State = "Airborne";
        }).CallDeferred();
    }

    /// <summary>
    /// Firing happens here rather than in a rollback tick, and only on the peer that owns the input. The weapon sends
    /// an RPC, and a rollback tick runs again for every resimulated tick - which would fire again each time.
    /// </summary>
    public override void _Process(double delta)
    {
        if (!IsLocal) return;

        var justPressed = Input.Fire && !_fireHeld;
        _fireHeld = Input.Fire;
        if (justPressed) Weapon.Fire();
    }

    private bool _fireHeld;

    /// <summary>
    /// Tells netfox how to encode each property instead of leaving it on the general-purpose variant encoding, which
    /// carries a type tag per value and sizes everything for the worst case.
    /// <para>
    /// Both peers have to agree, which is why this is code both of them run rather than a scene setting. Anything not
    /// listed stays on variant. Only the input direction is lossy here: half precision on a value that never
    /// accumulates is invisible, while a velocity that gathers gravity over many ticks is not the place for it.
    /// </para>
    /// </summary>
    private void ApplySchema() => Synchronizer.SetSchema(new Dictionary<string, NetworkSchemaSerializer>
    {
        [":position"] = NetworkSchemas.Vec3F32(),
        [":velocity"] = NetworkSchemas.Vec3F32(),
        [":JumpsLeft"] = NetworkSchemas.Uint8(),
        [":JumpHeld"] = NetworkSchemas.Bool8(),
        ["Input:Movement"] = NetworkSchemas.Vec2F16(),
        ["Input:Jump"] = NetworkSchemas.Bool8(),
    });

    /// <summary>Horizontal movement from this tick's input, keeping the vertical component it was handed.</summary>
    public Vector3 WithInput(Vector3 velocity)
    {
        velocity.X = Input.Movement.X * Speed;
        velocity.Z = Input.Movement.Y * Speed;
        return velocity;
    }

    /// <summary>True on the tick jump goes down, false while it stays down. Also records it for the next tick.</summary>
    public bool ConsumeJump()
    {
        var justPressed = Input.Jump && !JumpHeld;
        JumpHeld = Input.Jump;
        return justPressed;
    }

    /// <summary>
    /// Moves at the tick's speed. MoveAndSlide assumes the delta of whatever frame it is called from, which is not
    /// the tick delta a rollback runs at; PhysicsFactor is the ratio between the two, for both kinds of frame.
    /// </summary>
    public void Move(Vector3 velocity, double delta)
    {
        var factor = (float)NetworkTime.Instance.PhysicsFactor;
        Velocity = velocity * factor;
        MoveAndSlide();
        Velocity /= factor;
    }

    /// <summary>
    /// IsOnFloor only updates during MoveAndSlide. A rewind restores the position but not the flag, so the first read
    /// of a resimulated tick would be whatever the last pass left behind - a zero length move refreshes it.
    /// </summary>
    public void RefreshIsOnFloor()
    {
        var velocity = Velocity;
        Velocity = Vector3.Zero;
        MoveAndSlide();
        Velocity = velocity;
    }
}
