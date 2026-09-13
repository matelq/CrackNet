using Godot;
using Netfox.Extras;

namespace Netfox.Examples.Playground;

/// <summary>
/// Turns on rollback of real rigid bodies when the Rapier extension is installed, and stays out of the way when it is
/// not - so the sample runs either way.
/// <para>
/// This is the one thing stock Godot cannot do at all. Rollback advances the game several times inside one frame, and
/// Godot's physics server only steps in <c>_PhysicsProcess</c>: there is no way to step it by hand
/// (<see href="https://github.com/godotengine/godot/pull/76462">PR 76462</see> is not in a release). Rapier exposes
/// manual stepping and whole-space snapshots, which is what <see cref="RapierPhysicsDriver3D"/> drives - so crates
/// can be pushed, rewound and resimulated like anything else.
/// </para>
/// <para>
/// To turn it on: unzip godot-rapier-3d into <c>addons/godot-rapier3d</c> and set
/// <c>physics/3d/physics_engine="Rapier3D"</c>, or run <c>sh tools/install-extensions.sh rapier --enable-rapier</c>.
/// </para>
/// <para>
/// Rapier is <i>locally</i> deterministic - the same build on the same machine reproduces a run exactly - which is
/// all netfox asks of it: it replicates state rather than replaying inputs, so peers never have to agree bit for bit
/// on what physics produced. Cross platform determinism is a separate claim, needs Rapier's
/// <c>enhanced-determinism</c> feature, and a downloaded binary does not carry it.
/// </para>
/// </summary>
public partial class PhysicsTier : Node
{
    [Export] public PackedScene CrateScene { get; set; } = null!;
    [Export] public Node3D CrateRoot { get; set; } = null!;
    [Export] public int Crates { get; set; } = 3;

    /// <summary>
    /// Physics steps per network tick. The driver defaults to two; one is enough here and halves what a resimulation
    /// costs, which matters because a rewind steps the whole space again for every tick of the range.
    /// </summary>
    [Export] public int PhysicsFactor { get; set; } = 1;

    /// <summary>Whether the tier came up, for the status line.</summary>
    public bool Active { get; private set; }

    /// <summary>Why it did not, when it did not.</summary>
    public string Reason { get; private set; } = "";

    public override void _Ready()
    {
        if (!RapierPhysicsDriver3D.IsAvailable)
        {
            Reason = "godot-rapier3d not installed";
            return;
        }

        var engine = (string)ProjectSettings.GetSetting("physics/3d/physics_engine", "DEFAULT");
        if (engine != "Rapier3D")
        {
            // The extension can be present while Godot still owns the space, and then the driver would step a space
            // that has no Rapier state behind it
            Reason = $"physics/3d/physics_engine is {engine}, not Rapier3D";
            return;
        }

        AddChild(new RapierPhysicsDriver3D { Name = "RapierPhysicsDriver3D", PhysicsFactor = PhysicsFactor });
        NetworkRollback.Instance.AfterPrepareTick += FreezeHeldCrates;

        // After NetworkTime's _Process, which is where interpolation writes the players' displayed positions: a
        // held crate is drawn from its holder's displayed position, so it has to go last
        ProcessPriority = 100;
        SpawnCrates();
        Active = true;
    }

    /// <summary>
    /// Crates are named by index rather than by spawn order, because netfox addresses nodes by their path: a crate
    /// the host calls Crate_2 has to be Crate_2 on every peer.
    /// </summary>
    private void SpawnCrates()
    {
        for (var i = 0; i < Crates; i++)
        {
            var crate = CrateScene.Instantiate<NetworkRigidBody3D>();
            crate.Name = $"Crate_{i}";
            crate.Position = new Vector3(-3 + i * 2, 1.5f, 4);
            CrateRoot.AddChild(crate);
        }
    }

    public override void _ExitTree()
    {
        // C# events are not disconnected when a node is freed the way signals are
        if (NetworkRollback.Instance is { } rollback) rollback.AfterPrepareTick -= FreezeHeldCrates;
    }

    /// <summary>
    /// A crate is frozen exactly while some player's HeldCrate names it, decided after every restore from every
    /// player's replicated state. Freeze is not part of the crate's PhysicsState, so it cannot be rolled back;
    /// deriving it from state that is rolled back is the next best thing - and the only thing that unfreezes a crate
    /// on a client whose predicted grab the authority refused.
    /// </summary>
    private void FreezeHeldCrates(int tick)
    {
        var held = new HashSet<int>();
        foreach (var player in Players()) if (player.HeldCrate >= 0) held.Add(player.HeldCrate);

        var index = 0;
        foreach (var crate in CrateBodies()) Carried(crate, held.Contains(index++));
    }

    /// <summary>
    /// A held crate is out of the physics world: frozen, and on no collision layer, so nothing is left standing
    /// where it was picked up. The body itself is not moved while held - its position is not a fact anyone
    /// replicates, and it re-enters the world where the throw puts it.
    /// </summary>
    public static void Carried(RigidBody3D crate, bool held)
    {
        var layer = held ? 0u : 1u;
        crate.Freeze = held;
        crate.CollisionLayer = layer;
        crate.CollisionMask = layer;

        // The node's setters skip a value that has not changed, and the body's may have: a whole-space rollback
        // restores the body's mode and layers from the snapshot as well, behind the node's back
        PhysicsServer3D.BodySetMode(crate.GetRid(), held ? PhysicsServer3D.BodyMode.Static : PhysicsServer3D.BodyMode.Rigid);
        PhysicsServer3D.BodySetCollisionLayer(crate.GetRid(), layer);
        PhysicsServer3D.BodySetCollisionMask(crate.GetRid(), layer);
    }

    /// <summary>
    /// Draws every held crate at its holder's hand, every frame, after interpolation has placed the holder. This is
    /// what a peer sees, and it comes from replicated state alone - who holds what - never from the crate's own
    /// position, which while held is stale by a round trip on everyone but the host and used to snap the crate to
    /// the floor and back on the holder's own screen (netfox-net#59).
    /// </summary>
    public override void _Process(double delta)
    {
        if (!Active) return;
        var crates = CrateBodies();
        foreach (var player in Players())
            if (player.HeldCrate >= 0 && player.HeldCrate < crates.Count)
                crates[player.HeldCrate].GlobalPosition = player.Hand;
    }

    private IEnumerable<PlayerCharacter> Players()
        => CrateRoot.GetParent().GetNodeOrNull("Players")?.GetChildren().OfType<PlayerCharacter>() ?? [];

    private List<RigidBody3D> CrateBodies()
        => CrateRoot.GetChildren().OfType<RigidBody3D>().OrderBy(crate => crate.Name.ToString(), StringComparer.Ordinal).ToList();
}
