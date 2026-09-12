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
        RapierStateManager.Rollback(_stateManager, PhysicsSpace, tick, ref _storedStates);
    }
}

/// <summary>2D counterpart of RapierPhysicsDriver3D. Port of netfox.extras/physics/rapier_driver_2d.gd.</summary>
[GlobalClass]
public partial class RapierPhysicsDriver2D : PhysicsDriver
{
    private const string ServerClass = "RapierPhysicsServer2D";
    private const string StateManagerClass = "StateManager2D";

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

    protected override void SnapshotSpace(int tick) => _stateManager?.Call("cache_state", PhysicsSpace, tick);

    protected override void RollbackSpace(int tick)
    {
        if (_stateManager is null) return;
        RapierStateManager.Rollback(_stateManager, PhysicsSpace, tick, ref _storedStates);
    }
}

internal static class RapierStateManager
{
    public static Node Create(Node root, string className)
    {
        var manager = (Node)ClassDB.Instantiate(className).AsGodotObject();
        manager.Set("root_node", root);
        manager.Call("set_max_cache_length", NetfoxSettings.Instance.RollbackHistoryLimit);
        manager.Call("set_rolling_cache", true);
        return manager;
    }

    /// <summary>With a rolling cache, states are ordered by age with the newest at offset 0.</summary>
    public static void Rollback(Node manager, Rid space, int tick, ref int storedStates)
    {
        var offset = NetworkTime.Instance.Tick - tick;
        if (offset >= storedStates) return;

        var maxCacheLength = manager.Get("max_cache_length").AsInt32();
        storedStates = Math.Min(storedStates + 1, maxCacheLength);
        manager.Call("load_cached_state", space, offset);
    }
}
