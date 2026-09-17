using Godot;
using Netfox.Examples.Playground;
using Netfox.Internal;

namespace Netfox.Tests;

/// <summary>
/// A playground crate that stops being simulated here must stay where it is. Under Rapier, freezing a body that was
/// last kinematic somewhere else put it back there: a thrown crate returning to the host jumped to where it had been
/// held, inside the thrower, and was blown hundreds of metres away.
/// </summary>
public partial class CrateFreezeTests : TestSuite
{
    public override Task AfterCase()
    {
        FreeChildren();
        return Task.CompletedTask;
    }

    private async Task PhysicsFrames(int count)
    {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    [Test]
    public async Task FreezingKeepsTheCrateWhereItWasSimulatedTo()
    {
        var floor = new StaticBody3D { Name = "Floor", Position = new Vector3(0, -0.5f, 0) };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(100, 1, 100) } });
        await Mount(floor);

        var crate = PlaygroundCrate.Create("Crate", new Vector3(0, 3, 0));
        await Mount(crate);

        // Held: kinematic, moved by hand to where it is released
        PhysicsHandling.SetFrozen(crate, true);
        crate.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(0, 3, 0));
        await PhysicsFrames(3);

        // Thrown: simulated, it flies and lands somewhere else
        PhysicsHandling.SetFrozen(crate, false);
        crate.LinearVelocity = new Vector3(8, 0, 0);
        await PhysicsFrames(60);
        var landed = crate.GlobalPosition;
        Expect.True(landed.X > 2, $"the crate never flew: {landed}");

        // Handed back: frozen again here, and it must not go back to where it was held
        PhysicsHandling.SetFrozen(crate, true);
        await PhysicsFrames(3);
        Expect.True(crate.GlobalPosition.DistanceTo(landed) < 0.05f, $"landed at {landed}, frozen at {crate.GlobalPosition}");
    }
}
