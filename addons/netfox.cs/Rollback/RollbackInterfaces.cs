namespace Netfox;

/// <summary>Implemented by nodes that take part in rollback simulation. Replaces the duck-typed _rollback_tick.</summary>
public interface IRollbackTick
{
    /// <param name="delta">Tick duration in seconds.</param>
    /// <param name="tick">The tick being simulated.</param>
    /// <param name="isFresh">False when this tick has already been simulated before for this node.</param>
    void RollbackTick(double delta, int tick, bool isFresh);
}

/// <summary>Called when rollback restores a tick where this node is alive after having been despawned. Replaces _rollback_spawn.</summary>
public interface IRollbackSpawnAware
{
    void RollbackSpawn();
}

/// <summary>Called when rollback restores a tick where this node is not alive. Replaces _rollback_despawn.</summary>
public interface IRollbackDespawnAware
{
    void RollbackDespawn();
}

/// <summary>Called once the node can no longer be respawned by rollback. Replaces _rollback_destroy; the default is QueueFree.</summary>
public interface IRollbackDestroyAware
{
    void RollbackDestroy();
}

/// <summary>Editor-time property discovery, replaces _get_rollback_state_properties.</summary>
public interface IRollbackStateProperties
{
    IEnumerable<string> GetRollbackStateProperties();
}

/// <summary>Editor-time property discovery, replaces _get_rollback_input_properties.</summary>
public interface IRollbackInputProperties
{
    IEnumerable<string> GetRollbackInputProperties();
}

/// <summary>Editor-time property discovery, replaces _get_synchronized_state_properties.</summary>
public interface ISynchronizedStateProperties
{
    IEnumerable<string> GetSynchronizedStateProperties();
}

/// <summary>Editor-time property discovery, replaces _get_interpolated_properties.</summary>
public interface IInterpolatedProperties
{
    IEnumerable<string> GetInterpolatedProperties();
}
