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
/// <c>physics/3d/physics_engine="Rapier3D"</c>. Its single build per dimension is already cross platform
/// deterministic - there is no determinism option to find.
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
}
