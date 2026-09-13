using Godot;
using Netfox.Internal;

namespace Netfox.Extras;

/// <summary>2D counterpart of RapierPhysicsDriver3D. Port of netfox.extras/physics/rapier_driver_2d.gd.</summary>
[GlobalClass]
public partial class RapierPhysicsDriver2D : PhysicsDriver
{
    private const string ServerClass = "RapierPhysicsServer2D";
    private const string StateManagerClass = "StateManager2D";

    public static bool IsAvailable => ClassDB.ClassExists(ServerClass) && ClassDB.ClassExists(StateManagerClass);

    private Node? _stateManager;

    protected override void InitPhysicsSpace()
    {
        if (!IsAvailable)
        {
            Logger.Error("Rapier physics is not available! Is the extension installed?");
            return;
        }

        PhysicsSpace = GetViewport().World2D.Space;
        PhysicsServer2D.SpaceSetActive(PhysicsSpace, false);

        _stateManager = RapierStateManager.Create(this, StateManagerClass);
        AddChild(_stateManager);
    }

    protected override void PhysicsStep(double delta)
    {
        if (_stateManager is null) return;
        ClassDB.ClassCallStatic(ServerClass, "space_step", PhysicsSpace, delta);
        ClassDB.ClassCallStatic(ServerClass, "space_flush_queries", PhysicsSpace);
    }

    public override void FlushQueries()
    {
        if (_stateManager is not null) ClassDB.ClassCallStatic(ServerClass, "space_flush_queries", PhysicsSpace);
    }

    protected override void SnapshotSpace(int tick)
    {
        if (_stateManager is not null) RapierStateManager.Snapshot(_stateManager, PhysicsSpace, tick);
    }

    protected override void RollbackSpace(int tick)
    {
        if (_stateManager is null) return;
        if (!RapierStateManager.Rollback(_stateManager, PhysicsSpace, tick))
            Logger.Warning("No physics snapshot for tick {0}, the space was not rolled back", tick);
    }
}
