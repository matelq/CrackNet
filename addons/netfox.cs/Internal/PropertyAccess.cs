using Godot;

namespace Netfox.Internal;

/// <summary>
/// Reading and writing node properties on the hot path. Record and restore touch every property of every subject on
/// every tick, and <c>GetIndexed</c> parses its path each time.
/// <para>
/// A plain property path is resolved once to a <see cref="StringName"/> and read through <c>Get</c> / <c>Set</c>
/// instead. Measured on a Node3D position over 200k get and set pairs: 167ms indexed, 50ms through this cache, 42ms
/// with the name already in hand. Nested paths such as "position:x" keep the indexed access.
/// </para>
/// <para>Not thread safe, like the rest of netfox: everything here runs on the main loop.</para>
/// </summary>
internal static class PropertyAccess
{
    private static readonly Dictionary<NodePath, StringName?> DirectNames = new();

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

        DirectNames[property] = name;
        return name;
    }
}
