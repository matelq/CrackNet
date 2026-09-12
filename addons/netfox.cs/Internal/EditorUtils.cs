using Godot;

namespace Netfox.Internal;

/// <summary>Editor-time discovery of properties declared by nodes through the I*Properties interfaces. Port of netfox.internals/editor-utils.gd.</summary>
internal static class EditorUtils
{
    /// <summary>
    /// Walks <paramref name="root"/> and its descendants; for every node implementing <typeparamref name="TInterface"/>
    /// passes each declared property name to <paramref name="handler"/> together with the declaring node. Returns warnings.
    /// </summary>
    public static string[] GatherProperties<TInterface>(Node root, Func<TInterface, IEnumerable<string>?> getter, Action<Node, string> handler)
        where TInterface : class
    {
        var warnings = new List<string>();

        var nodes = new List<Node> { root };
        foreach (var child in root.FindChildren("*"))
            nodes.Add(child);

        foreach (var node in nodes)
        {
            if (node is not TInterface declaring) continue;

            var readableName = $"\"{node.Name}\" (\"{root.GetPathTo(node)}\")";
            var props = getter(declaring);
            if (props is null)
            {
                warnings.Add($"Node {readableName} returned no properties for {typeof(TInterface).Name}");
                continue;
            }

            foreach (var prop in props)
            {
                if (string.IsNullOrEmpty(prop))
                {
                    warnings.Add($"Node {readableName} specified an empty property in {typeof(TInterface).Name}");
                    continue;
                }
                handler(node, prop);
            }
        }

        return warnings.ToArray();
    }
}
