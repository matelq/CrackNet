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
        var players = CrateRoot.GetParent().GetNodeOrNull("Players");
        if (players is null) return;

        var held = new HashSet<int>();
        foreach (var child in players.GetChildren())
            if (child is PlayerCharacter { HeldCrate: >= 0 } player) held.Add(player.HeldCrate);

        var index = 0;
        foreach (var child in CrateRoot.GetChildren().OfType<RigidBody3D>().OrderBy(crate => crate.Name.ToString(), StringComparer.Ordinal))
        {
            var frozen = held.Contains(index++);
            if (child.Freeze != frozen) child.Freeze = frozen;
        }
    }
}
