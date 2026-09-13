using Godot;
using Netfox.Internal;

namespace Netfox;

/// <summary>The engine-touching half of the Core data types: reading and writing node properties, authority checks.</summary>
public static class GodotDataExtensions
{
    public static void RecordProperty(this Snapshot snapshot, Node subject, NodePath property)
        => snapshot.SetProperty(subject, property, subject.GetValue(property));

    /// <summary>Writes every stored value back onto its subject.</summary>
    public static void Apply(this Snapshot snapshot)
    {
        foreach (var subject in snapshot.Subjects)
            foreach (var (property, value) in snapshot.GetSubjectData(subject))
                subject.SetValue(property, value);
    }

    /// <summary>Drops every subject that <paramref name="sender"/> does not have authority over.</summary>
    public static void Sanitize(this Snapshot snapshot, int sender)
        => snapshot.Sanitize(subject => subject.GetMultiplayerAuthority() == sender);

    public static void RecordProperty(this ObjectSnapshot snapshot, NodePath property)
        => snapshot.SetValue(property, snapshot.Subject.GetValue(property));

    public static void Apply(this ObjectSnapshot snapshot)
    {
        foreach (var (property, value) in snapshot.Data)
            snapshot.Subject.SetValue(property, value);
    }

    /// <summary>Replaces the pool contents with the parsed "node:property" paths relative to <paramref name="root"/>.</summary>
    public static void SetFromPaths(this PropertyPool pool, Node root, IEnumerable<string> paths)
    {
        pool.Clear();
        foreach (var path in paths)
        {
            var entry = PropertyEntry.Parse(root, path);
            pool.Add(entry.Node, entry.Property);
        }
    }
}
