using Godot;
using Netfox.Core.Logging;

namespace Netfox;

/// <summary>Parses "Node/Path:property" strings relative to a root node. Port of properties/property-entry.gd.</summary>
public sealed class PropertyEntry
{
    private static readonly NetfoxLogger Logger = NetfoxLogger.ForNetfox("PropertyEntry");

    public string Path { get; }
    public Node Node { get; }
    public NodePath Property { get; }

    private PropertyEntry(string path, Node node, NodePath property)
    {
        Path = path;
        Node = node;
        Property = property;
    }

    public Variant GetValue() => Node.GetIndexed(Property);

    public void SetValue(Variant value) => Node.SetIndexed(Property, value);

    public bool IsValid()
    {
        if (Node is null || !GodotObject.IsInstanceValid(Node)) return false;
        var name = Property.ToString();
        foreach (var info in Node.GetPropertyList())
            if (info["name"].AsString() == name) return true;
        return false;
    }

    public override string ToString() => Path;

    /// <summary>The part before the colon is a node path relative to <paramref name="root"/>, the rest is the property.</summary>
    public static PropertyEntry Parse(Node root, string path)
    {
        var colon = path.IndexOf(':');
        var nodePath = colon < 0 ? path : path.Substring(0, colon);
        var property = colon < 0 ? "" : path.Substring(colon + 1);
        var node = nodePath.Length == 0 || nodePath == "." ? root : root.GetNode(nodePath);
        return new PropertyEntry(path, node, new NodePath(property));
    }

    /// <summary>Builds a "node:property" path string. Node may be a string, NodePath, or Node relative to <paramref name="root"/>.</summary>
    public static string MakePath(Node root, object node, string property)
    {
        var nodePath = node switch
        {
            string s => s,
            NodePath np => np.ToString(),
            Node n => root.GetPathTo(n).ToString(),
            _ => null,
        };

        if (nodePath is null)
        {
            Logger.Error("Cannot stringify node reference: {0}", node);
            return "";
        }

        if (nodePath == ".") nodePath = "";
        return $"{nodePath}:{property}";
    }
}
