using Godot;

namespace Netfox.Internal;

/// <summary>
/// Reading and writing node properties on the hot path. Record and restore touch every property of every subject on
/// every tick, and <c>GetIndexed</c> parses its path each time.
/// <para>
/// A plain property path is resolved once to a <see cref="StringName"/> and read through <c>Get</c> / <c>Set</c>
/// instead. Measured on a Node3D position over 200k get and set pairs: 237ms indexed, 44ms through this cache, 23ms for
/// a typed C# property. Nested paths such as "position:x" keep the indexed access.
/// </para>
/// <para>Not thread safe, like the rest of netfox: everything here runs on the main loop.</para>
/// </summary>
internal static class PropertyAccess
{
    // Keyed by reference, not by value: hashing a NodePath goes through the engine and costs more than the call it
    // saves. The paths come from property pools, which hold on to their instances, so lookups hit. A miss only adds an
    // entry, so correctness does not depend on that; the cap is there for callers that build paths on the fly.
    private const int MaxEntries = 4096;
    private static readonly Dictionary<NodePath, StringName?> DirectNames = new(ReferenceEqualityComparer.Instance);

    public static Variant GetValue(this Node subject, NodePath property)
    {
        var name = DirectNameOf(property);
        return name is not null ? subject.Get(name) : subject.GetIndexed(property);
    }

    public static void SetValue(this Node subject, NodePath property, Variant value)
    {
        var name = DirectNameOf(property);
        if (name is not null) subject.Set(name, value);
        else subject.SetIndexed(property, value);
    }

    /// <summary>The property name to use with Get and Set, or null when the path has to go through GetIndexed.</summary>
    public static StringName? DirectNameOf(NodePath property)
    {
        if (DirectNames.TryGetValue(property, out var cached)) return cached;

        var name = property.GetNameCount() == 1 && property.GetSubNameCount() == 0
            ? new StringName(property.ToString())
            : null;

        if (DirectNames.Count >= MaxEntries) DirectNames.Clear();
        DirectNames[property] = name;
        return name;
    }
}
