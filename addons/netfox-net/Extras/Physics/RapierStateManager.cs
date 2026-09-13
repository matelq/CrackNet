using Godot;
using Netfox.Internal;

namespace Netfox.Extras;

/// <summary>
/// The Rapier extension's StateManager2D/3D, driven through ClassDB. Snapshots are tagged with the tick they were
/// taken for and found again by that tag, through <c>ordered_cache_tags</c>.
/// <para>
/// Upstream's <c>.off</c> driver addressed the cache by age instead - "offset 0 is the newest" - behind a counter of
/// stored states that nothing ever incremented, so it returned before loading anything, every time. This port
/// reproduced that faithfully, and RapierCheck passed anyway because it never actually rolled back (netfox-net#62).
/// </para>
/// </summary>
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

    public static void Snapshot(Node manager, Rid space, int tick) => manager.Call("cache_state", space, tick);

    /// <summary>
    /// Restores the space to the snapshot taken for <paramref name="tick"/>, and drops everything cached after it:
    /// the resimulation that follows takes those snapshots again, and a cache that kept both copies would hand back
    /// the stale one on the next rollback. Returns false when no snapshot for that tick exists.
    /// </summary>
    public static bool Rollback(Node manager, Rid space, int tick)
    {
        var tags = manager.Call("ordered_cache_tags").AsGodotArray();
        var index = -1;
        for (var i = 0; i < tags.Count; i++)
            if (tags[i].VariantType == Variant.Type.Int && tags[i].AsInt32() == tick) index = i;
        if (index < 0) return false;

        manager.Call("load_cached_state", space, index);
        for (var i = tags.Count - 1; i > index; i--)
            manager.Call("remove_cached_by_index", i);
        return true;
    }
}
