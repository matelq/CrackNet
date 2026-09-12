using Godot;
using Netfox.Internal;

namespace Netfox.Extras;

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
    public static void Rollback(Node manager, Rid space, int offset, ref int storedStates)
    {
        if (offset >= storedStates) return;

        var maxCacheLength = manager.Get("max_cache_length").AsInt32();
        storedStates = Math.Min(storedStates + 1, maxCacheLength);
        manager.Call("load_cached_state", space, offset);
    }
}
