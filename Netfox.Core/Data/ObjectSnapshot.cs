namespace Netfox.Core.Data;

/// <summary>Snapshot data for a single object. Port of servers/data/object-snapshot.gd.</summary>
public sealed class ObjectSnapshot<TSubject, TProperty, TValue>
    where TSubject : class
    where TProperty : notnull
{
    private readonly Dictionary<TProperty, TValue> _data = new();

    public TSubject Subject { get; }
    public bool IsAuth { get; set; }

    public ObjectSnapshot(TSubject subject)
    {
        Subject = subject;
    }

    public ObjectSnapshot<TSubject, TProperty, TValue> Duplicate()
    {
        var result = new ObjectSnapshot<TSubject, TProperty, TValue>(Subject) { IsAuth = IsAuth };
        foreach (var (property, value) in _data)
            result._data[property] = value;
        return result;
    }

    public TValue? GetValue(TProperty property, TValue? fallback = default)
        => _data.TryGetValue(property, out var value) ? value : fallback;

    public bool TryGetValue(TProperty property, out TValue value) => _data.TryGetValue(property, out value!);

    public void SetValue(TProperty property, TValue value) => _data[property] = value;

    public bool HasValue(TProperty property) => _data.ContainsKey(property);

    public IReadOnlyCollection<TProperty> Properties => _data.Keys;

    public IReadOnlyDictionary<TProperty, TValue> Data => _data;

    public override string ToString()
        => $"ObjectSnapshot({Subject}, {IsAuth}, [{string.Join(", ", _data.Select(kv => $"{kv.Key}: {kv.Value}"))}])";
}
