using Godot;

namespace Netfox.Examples.Playground;

/// <summary>
/// Something that moves and that nobody controls: it stands still until a player comes close, then runs.
/// <para>
/// This is the shape of every AI-driven thing in a game, and it is the one rollback root in the sample with
/// <b>no input at all</b>. Its "AI" is a steering function in <see cref="RollbackTick"/> - a pure function of state:
/// where the players are, where it is, which way it was heading. No wall clock, no random numbers that are not
/// rewindable, nothing read from outside the tick. That is what makes it reproducible, and being reproducible is what
/// lets the host roll it back when a player's late input turns out to have chased it a different way.
/// </para>
/// <para>
/// Authority is the host, and <b>only the host runs the rule</b>. That needs saying because netfox's default is the
/// opposite: a rollback node with no input is simulated on every peer, on the theory that everyone can run a rule
/// that needs no input. For a platform on a fixed path that is right. For something that reacts to players it means
/// every client extrapolates it from its own predicted view of the players and is corrected every tick - which is
/// prediction, and for an object nobody owns the honest answer is not to. The guard at the top of
/// <see cref="RollbackTick"/> is that decision; clients keep the state the host sent and interpolate it, one round
/// trip late.
/// </para>
/// <para>
/// The synchronizer and interpolator are created here rather than in a scene, so adding one is one line in
/// <see cref="Playground"/> and both peers get identical trees.
/// </para>
/// </summary>
public partial class Npc : CharacterBody3D, IRollbackTick
{
    [Export] public float Speed { get; set; } = 3.0f;
    [Export] public float FleeDistance { get; set; } = 6.0f;

    /// <summary>Which way it is going. State, not a field: a rewind puts it back on the heading it had that tick.</summary>
    public Vector3 Heading { get; set; } = Vector3.Forward;

    public RollbackSynchronizer Synchronizer { get; private set; } = null!;

    /// <summary>Ticks this peer simulated. Zero on a client is the whole point.</summary>
    public int SimulatedTicks { get; private set; }

    private Node? _players;
    private float _gravity;

    private Node3D _visual = null!;

    /// <summary>Turns the visual to the heading. Display only: rotation is not state and the body is never rotated.</summary>
    public override void _Process(double delta)
    {
        if (Heading.LengthSquared() > 0.01f) _visual.Rotation = new Vector3(0, Mathf.Atan2(Heading.X, Heading.Z), 0);
    }

    public override void _Ready()
    {
        _gravity = (float)(double)ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8);
        _players = GetTree().Root.FindChild("Players", recursive: true, owned: false);
        PlatformFloorLayers = 0;

        // On no layer at all, so players pass through it: clients do not simulate it, so their copy is at least one
        // tick stale even with no latency, and a predicted body that touches a stale one slides differently on each
        // peer - 10cm apart for good, measured. An object that is told where it is must not be something predicted
        // bodies collide with. The mask still has the ground, so it stands on it.
        CollisionLayer = 0;
        CollisionMask = 1;
        AddChild(new CollisionShape3D { Name = "CollisionShape3D", Shape = new CapsuleShape3D { Radius = 0.35f, Height = 1.2f } });

        // Something to look at: the first NPC had a collision shape and nothing else, and read as "not seen" in the
        // first two-window session (netfox-net#60). The nose is on the visual's +Z and _Process turns the visual to
        // the heading, so which way it is running is readable - the body itself is not rotated, since its transform
        // is what the physics side is told every tick.
        _visual = new Node3D { Name = "Visual" };
        _visual.AddChild(new MeshInstance3D
        {
            Name = "Mesh",
            Mesh = new CapsuleMesh { Radius = 0.35f, Height = 1.2f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.55f, 0.2f) },
        });
        _visual.AddChild(new MeshInstance3D
        {
            Name = "Nose",
            Mesh = new BoxMesh { Size = new Vector3(0.15f, 0.15f, 0.3f) },
            Position = new Vector3(0, 0.3f, 0.45f),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.1f, 0.05f) },
        });
        AddChild(_visual);

        Synchronizer = new RollbackSynchronizer
        {
            Name = "RollbackSynchronizer",
            Root = this,
            StateProperties = [":position", ":velocity", ":Heading"],
        };
        AddChild(Synchronizer);
        AddChild(new TickInterpolator { Name = "TickInterpolator", Root = this, Properties = [":position"] });
    }

    public void RollbackTick(double delta, int tick, bool isFresh)
    {
        // Everyone is asked to simulate a node with no input; only the authority should. See the class summary.
        if (!IsMultiplayerAuthority()) return;
        SimulatedTicks++;

        var away = Vector3.Zero;
        if (_players is not null)
            foreach (var child in _players.GetChildren())
            {
                if (child is not Node3D player) continue;
                var offset = GlobalPosition - player.GlobalPosition;
                offset.Y = 0;
                var distance = offset.Length();
                if (distance < FleeDistance && distance > 0.01f) away += offset / distance * (FleeDistance - distance);
            }

        // Run if anyone is close, stand otherwise. Standing when calm is deliberate and not only for flavour: a
        // replicated object that never stops moving is, on a client, always one round trip behind - which is display
        // latency, not disagreement, and a tick-by-tick comparison of what each peer believed at the time cannot tell
        // the two apart. An object that comes to rest can be compared fairly.
        var spooked = away.LengthSquared() > 0.01f;
        if (spooked) Heading = away.Normalized();

        var velocity = Velocity;
        velocity.X = spooked ? Heading.X * Speed : 0;
        velocity.Z = spooked ? Heading.Z * Speed : 0;
        velocity.Y = IsOnFloor() ? 0 : velocity.Y - _gravity * (float)delta;

        // Same trick as the players: MoveAndSlide assumes the frame's delta, and a tick is not a frame
        var factor = (float)NetworkTime.Instance.PhysicsFactor;
        Velocity = velocity * factor;
        MoveAndSlide();
        Velocity /= factor;
    }
}
