namespace Netfox.Core.Data;

/// <summary>A set of properties, each belonging to a subject. Port of servers/data/property-pool.gd.</summary>
public sealed class PropertyPool<TSubject, TProperty>
    where TSubject : class
    where TProperty : notnull
{
    private readonly Dictionary<TSubject, List<TProperty>> _propertiesBySubject = new(ReferenceEqualityComparer.Instance);

    public static PropertyPool<TSubject, TProperty> Of(IEnumerable<(TSubject subject, TProperty property)> entries)
    {
        var pool = new PropertyPool<TSubject, TProperty>();
        foreach (var (subject, property) in entries)
            pool.Add(subject, property);
        return pool;
    }

    public void Add(TSubject subject, TProperty property)
    {
        if (!_propertiesBySubject.TryGetValue(subject, out var props))
            _propertiesBySubject[subject] = props = new List<TProperty>();
        if (!props.Contains(property))
            props.Add(property);
    }

    public bool Has(TSubject subject, TProperty property)
        => _propertiesBySubject.TryGetValue(subject, out var props) && props.Contains(property);

    public bool HasSubject(TSubject subject) => _propertiesBySubject.ContainsKey(subject);

    public void Erase(TSubject subject, TProperty property)
    {
        if (!_propertiesBySubject.TryGetValue(subject, out var props)) return;
        props.Remove(property);
        if (props.Count == 0) _propertiesBySubject.Remove(subject);
    }

    public void EraseSubject(TSubject subject) => _propertiesBySubject.Remove(subject);

    public void Clear() => _propertiesBySubject.Clear();

    public IReadOnlyList<TProperty> GetPropertiesOf(TSubject subject)
        => _propertiesBySubject.TryGetValue(subject, out var props) ? props : Array.Empty<TProperty>();

    public IReadOnlyCollection<TSubject> Subjects => _propertiesBySubject.Keys;

    public bool IsEmpty => _propertiesBySubject.Count == 0;
}
