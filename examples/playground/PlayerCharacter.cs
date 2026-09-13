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

    /// <summary>
    /// Which crate this player is carrying, as its index under World/Crates, or -1. An index rather than a node
    /// reference because it has to go over the wire and come back out of history; every peer orders the crates the
    /// same way, so the index means the same crate everywhere.
    /// </summary>
    [RollbackState] public int HeldCrate { get; set; } = -1;

    /// <summary>Last direction walked in, for where a throw goes. Rollback state: a throw is a function of the tick.</summary>
    [RollbackState] public Vector3 Facing { get; set; } = Vector3.Forward;

    /// <summary>How far a crate may be to pick it up, and where it rides.</summary>
    [Export] public float GrabReach { get; set; } = 1.6f;
    [Export] public Vector3 CarryOffset { get; set; } = new(0, 1.3f, 0);
    [Export] public float ThrowSpeed { get; set; } = 7.0f;

    /// <summary>Where a held crate is: a function of this player's state, so every peer derives the same spot.</summary>
    // A metre ahead: the capsule has radius 0.4 and the crate a half-width of 0.5, and a crate released inside the
    // capsule is thrown by the solver, two metres in a random direction, before the throw velocity gets a say
    public Vector3 Hand => GlobalPosition + CarryOffset + Facing * 1.0f;

    /// <summary>
    /// The two events of carrying. Peers predict them in their rollback tick; the authority broadcasts what really
    /// happened and predictions get confirmed or cancelled - a cancelled pickup rolls the crate back to free.
    /// Created here rather than in the scene: both peers run this code, so both get nodes with these names and the
    /// paths line up, and the .tscn does not have to be touched.
    /// </summary>
    public RewindableAction GrabAction { get; private set; } = null!;
    public RewindableAction ThrowAction { get; private set; } = null!;

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

        // Godot carries a body standing on a moving platform by itself, and it has to be turned off here - see
        // RideFloor. Zero means no layer counts as a moving platform, so MoveAndSlide only ever moves this body by
        // its own velocity.
        PlatformFloorLayers = 0;

        GrabAction = new RewindableAction { Name = "GrabAction" };
        ThrowAction = new RewindableAction { Name = "ThrowAction" };
        AddChild(GrabAction);
        AddChild(ThrowAction);

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
        [":HeldCrate"] = NetworkSchemas.Int8(),
        [":Facing"] = NetworkSchemas.Vec3F32(),
        ["Input:Movement"] = NetworkSchemas.Vec2F16(),
        ["Input:Jump"] = NetworkSchemas.Bool8(),
        ["Input:Grab"] = NetworkSchemas.Bool8(),
        ["Input:Throw"] = NetworkSchemas.Bool8(),
    });

    /// <summary>
    /// Picking up, carrying and throwing a crate, once per tick from both states.
    /// <para>
    /// While held the crate is not simulated: it is frozen and placed relative to this player every tick, so the
    /// player's own prediction carries it for free and no round trip is involved. The two transitions are
    /// <see cref="RewindableAction"/>s, because they have to happen on one and the same tick on every peer and only
    /// the authority can say which - and the crate is <see cref="NetworkRollback.Mutate"/>d on each, because it has
    /// no input of its own and nothing else would make it resimulate the tick it changed hands.
    /// </para>
    /// <para>
    /// A client only calls SetActive from ticks that have its real input - a prediction set as an action would be
    /// taken as a claim. The authority calls it on every tick, predicted or not, because its verdict is the truth by
    /// definition and a tick it never rules on is a tick nobody gets a verdict for: a remote input lost on the wire
    /// leaves that tick predicted on the authority for good, and a client that predicted a pickup there would carry
    /// the crate forever with nobody ever having confirmed it. That happened, under 10% loss, on the third run.
    /// </para>
    /// </summary>
    public void Carry(int tick)
    {
        if (Input.Movement.LengthSquared() > 0.01f) Facing = new Vector3(Input.Movement.X, 0, Input.Movement.Y).Normalized();

        var crates = Crates();
        var predicting = Synchronizer.IsPredicting();

        if (!predicting || IsMultiplayerAuthority())
        {
            var wanted = HeldCrate < 0 && Input.Grab ? NearestCrate(crates) : -1;
            GrabAction.SetActive(wanted >= 0);
            if (wanted >= 0) GrabAction.SetContext(wanted);
            ThrowAction.SetActive(HeldCrate >= 0 && Input.Throw);
        }

        // State is applied on Confirming AND Active: a resimulated tick restores HeldCrate to what it was before
        // the grab and then runs this again, and the grab has to happen again. Only the side effect that must not
        // repeat - marking the crate for resimulation - is Confirming-only. Cancelling is the authority saying the
        // grab did not happen on this tick: the state it would have set is simply not set.
        var grab = GrabAction.GetStatus();
        if (grab is RewindableAction.Status.Confirming or RewindableAction.Status.Active
            && HeldCrate < 0 && GrabAction.GetContext<int>() is var index && index >= 0 && index < crates.Count)
        {
            HeldCrate = index;
            if (grab == RewindableAction.Status.Confirming) NetworkRollback.Instance.Mutate(crates[index]);
            PhysicsTier.Carried(crates[index], true); // out of the world from this tick on, not from the next restore
        }
        else if (grab == RewindableAction.Status.Cancelling && HeldCrate >= 0 && HeldCrate < crates.Count)
        {
            NetworkRollback.Instance.Mutate(crates[HeldCrate]);
            HeldCrate = -1;
        }

        var thrown = ThrowAction.GetStatus();
        if (thrown is RewindableAction.Status.Confirming or RewindableAction.Status.Active && HeldCrate >= 0 && HeldCrate < crates.Count)
        {
            var crate = crates[HeldCrate];
            if (thrown == RewindableAction.Status.Confirming) NetworkRollback.Instance.Mutate(crate);
            HeldCrate = -1;

            // The body re-enters the world here, at the hand and with the throw's velocity, through the same state
            // property history restores it by. While it was held nothing moved the body - see PhysicsTier - so
            // this is the one place the body learns where the carry took it. Setting the node's position instead
            // did not move the body under Rapier: the crate flew from where it had been picked up (netfox-net#59).
            // Back into the world first, then placed: the mode change is what re-creates the body on the engine
            // side, and a transform written before it does not survive it
            PhysicsTier.Carried(crate, false);
            if (crate is NetworkRigidBody3D networkCrate)
                networkCrate.PhysicsState = [Hand, Quaternion.Identity, Facing * ThrowSpeed + Vector3.Up * 2, Vector3.Zero, false];
        }

        // Where a held crate is drawn is not decided here either. Its position is not a replicated fact while it
        // is held - only who holds it is - and PhysicsTier places it from the holder every frame, after
        // interpolation, on every peer. Whether it is frozen and collides is derived there too, after each restore,
        // because neither is rollback state and a crate left frozen by a prediction the authority refused would
        // ignore every correction sent to it afterwards.
    }

    private int NearestCrate(List<RigidBody3D> crates)
    {
        var best = -1;
        var bestDistance = GrabReach;
        for (var i = 0; i < crates.Count; i++)
        {
            if (crates[i].Freeze) continue; // somebody else has it
            var distance = GlobalPosition.DistanceTo(crates[i].GlobalPosition);
            if (distance >= bestDistance) continue;
            best = i;
            bestDistance = distance;
        }
        return best;
    }

    /// <summary>The crates in the same order on every peer: sorted by name under World/Crates.</summary>
    private List<RigidBody3D> Crates()
        => GetTree().Root.FindChild("Crates", recursive: true, owned: false)?.GetChildren().OfType<RigidBody3D>()
            .OrderBy(crate => crate.Name.ToString(), StringComparer.Ordinal).ToList() ?? new List<RigidBody3D>();

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
        ShoveWhatWasHit(velocity);
    }

    /// <summary>How hard walking into a rigid body pushes it. A knob, not a law: tune it to the crates you have.</summary>
    [Export] public float ShoveImpulse { get; set; } = 0.35f;

    /// <summary>
    /// MoveAndSlide never moves another body - it slides this one around whatever it hits. So a crate a player walks
    /// into has to be pushed explicitly. Done here, inside the tick, from this tick's velocity and the contact normal
    /// and nothing else: a resimulated tick then shoves exactly as the first pass did, which is what lets the crate
    /// be rolled back through the physics driver along with everything else.
    /// </summary>
    private void ShoveWhatWasHit(Vector3 velocity)
    {
        if (velocity.LengthSquared() < 0.01f) return;
        for (var i = 0; i < GetSlideCollisionCount(); i++)
        {
            var collision = GetSlideCollision(i);
            if (collision.GetCollider() is not RigidBody3D body) continue;

            var push = -collision.GetNormal();
            push.Y = 0;
            if (push.LengthSquared() < 0.01f) continue;
            body.ApplyImpulse(push.Normalized() * ShoveImpulse, collision.GetPosition() - body.GlobalPosition);
        }
    }

    /// <summary>
    /// Moves with the platform underfoot, for the one tick given.
    /// <para>
    /// Godot does this on its own, and its way cannot be used here. It derives the platform's velocity from how far
    /// the platform moved between two <i>physics frames</i>, and applies it inside every <c>MoveAndSlide</c>. A tick
    /// is not a frame: a rollback runs several ticks inside one frame and calls MoveAndSlide twice per tick, so the
    /// same frame's worth of platform motion gets added over and over, and the rider is thrown off the platform. That
    /// is what <c>PlatformFloorLayers = 0</c> in _Ready switches off.
    /// </para>
    /// <para>
    /// What replaces it is a function of the tick, like everything else in a rollback tick has to be, so a
    /// resimulated tick carries the player exactly as far as the first pass did.
    /// </para>
    /// </summary>
    public void RideFloor(int tick)
    {
        foreach (var node in GetTree().GetNodesInGroup(MovingPlatform.Group))
        {
            if (node is not MovingPlatform platform) continue;
            if (!platform.CarriesAt(GlobalPosition, tick)) continue;

            // MoveAndCollide rather than an assignment to Position: the ride has to be a move like any other.
            // Writing the position directly pushes the rider straight through whatever is standing next to it on the
            // platform, and the two peers then dig the same two bodies into each other by different amounts - which
            // is the one way two players can shove each other here at all, since MoveAndSlide otherwise just slides
            // them apart.
            MoveAndCollide(platform.MotionAt(tick));
            return;
        }
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
