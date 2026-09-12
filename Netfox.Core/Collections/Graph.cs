namespace Netfox.Core.Collections;

/// <summary>Directed graph with lookup in both directions. Port of netfox.internals/graph.gd.</summary>
public sealed class Graph<T> where T : class
{
    private readonly Dictionary<T, HashSet<T>> _outgoing = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<T, HashSet<T>> _incoming = new(ReferenceEqualityComparer.Instance);

    private static readonly HashSet<T> Empty = new();

    public void Link(T from, T to)
    {
        Bucket(_outgoing, from).Add(to);
        Bucket(_incoming, to).Add(from);
    }

    public void Unlink(T from, T to)
    {
        RemoveFrom(_outgoing, from, to);
        RemoveFrom(_incoming, to, from);
    }

    /// <summary>Removes every link touching <paramref name="node"/>.</summary>
    public void Erase(T node)
    {
        if (_outgoing.Remove(node, out var targets))
            foreach (var target in targets) RemoveFrom(_incoming, target, node);
        if (_incoming.Remove(node, out var sources))
            foreach (var source in sources) RemoveFrom(_outgoing, source, node);
    }

    /// <summary>Nodes that link to <paramref name="node"/>.</summary>
    public IReadOnlyCollection<T> GetLinkedTo(T node) => _incoming.GetValueOrDefault(node) ?? Empty;

    /// <summary>Nodes that <paramref name="node"/> links to.</summary>
    public IReadOnlyCollection<T> GetLinkedFrom(T node) => _outgoing.GetValueOrDefault(node) ?? Empty;

    private static HashSet<T> Bucket(Dictionary<T, HashSet<T>> map, T key)
    {
        if (!map.TryGetValue(key, out var set))
            map[key] = set = new HashSet<T>(ReferenceEqualityComparer.Instance);
        return set;
    }

    private static void RemoveFrom(Dictionary<T, HashSet<T>> map, T key, T value)
    {
        if (!map.TryGetValue(key, out var set)) return;
        set.Remove(value);
        if (set.Count == 0) map.Remove(key);
    }
}
