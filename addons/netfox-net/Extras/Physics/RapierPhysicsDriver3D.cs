using Godot;
using Netfox.Internal;

namespace Netfox.Extras;

/// <summary>
/// Physics driver for the Rapier GDExtension (appsinacup/godot-rapier-physics): manual space stepping plus its
/// StateManager for whole-world snapshots. The extension has no C# bindings, so it is driven through ClassDB.
/// Port of netfox.extras/physics/rapier_driver_3d.gd.
/// </summary>
[GlobalClass]
public partial class RapierPhysicsDriver3D : PhysicsDriver
{
    private const string ServerClass = "RapierPhysicsServer3D";
    private const string StateManagerClass = "StateManager3D";

    public static bool IsAvailable => ClassDB.ClassExists(ServerClass) && ClassDB.ClassExists(StateManagerClass);

    private Node? _stateManager;
    private int _storedStates;

    protected override void InitPhysicsSpace()
    {
        if (!IsAvailable)
        {
            Logger.Error("Rapier physics is not available! Is the extension installed?");
            return;
        }

        PhysicsSpace = GetViewport().World3D.Space;
        PhysicsServer3D.SpaceSetActive(PhysicsSpace, false);

        _stateManager = RapierStateManager.Create(this, StateManagerClass);
        AddChild(_stateManager);
    }

    protected override void PhysicsStep(double delta)
    {
        if (_stateManager is null) return;
        ClassDB.ClassCallStatic(ServerClass, "space_step", PhysicsSpace, delta);
        ClassDB.ClassCallStatic(ServerClass, "space_flush_queries", PhysicsSpace);
    }

    protected override void SnapshotSpace(int tick) => _stateManager?.Call("cache_state", PhysicsSpace, tick);

    protected override void RollbackSpace(int tick)
    {
        if (_stateManager is null) return;
        RapierStateManager.Rollback(_stateManager, PhysicsSpace, Context.NetworkTime.Tick - tick, ref _storedStates);
    }
}
