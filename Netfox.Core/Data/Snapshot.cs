using System.Text;

namespace Netfox.Core.Data;

/// <summary>
/// Stores property values of multiple subjects, recorded for a specific tick.
/// Port of servers/data/snapshot.gd. Engine-specific operations (record from / apply to the subject,
/// authority checks) live in Netfox.Godot as extension methods.
/// </summary>
public sealed class Snapshot<TSubject, TProperty, TValue> : IEquatable<Snapshot<TSubject, TProperty, TValue>>
    where TSubject : class
    where TProperty : notnull
{
    /// <summary>Comparer used for value equality in patches, merges and Equals. Netfox.Godot sets a Variant-aware one.</summary>
    public static IEqualityComparer<TValue> ValueComparer { get; set; } = EqualityComparer<TValue>.Default;

    public int Tick { get; set; }

    private readonly Dictionary<TSubject, Dictionary<TProperty, TValue>> _data = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<TSubject> _authSubjects = new(ReferenceEqualityComparer.Instance);

    public Snapshot(int tick)
    {
        Tick = tick;
    }

    public static Snapshot<TSubject, TProperty, TValue> MakePatch(
        Snapshot<TSubject, TProperty, TValue> from,
        Snapshot<TSubject, TProperty, TValue> to,
        int? tick = null)
    {
        var patch = new Snapshot<TSubject, TProperty, TValue>(tick ?? to.Tick);

        foreach (var (subject, props) in to._data)
        {
            // Only patch to auth subjects
            if (!to.IsAuth(subject)) continue;

            foreach (var (property, value) in props)
            {
                if (!from.TryGetProperty(subject, property, out var fromValue) || !ValueComparer.Equals(fromValue, value))
                    patch.SetProperty(subject, property, value);
            }
            patch.SetAuth(subject, true);
        }

        return patch;
    }

    public static Snapshot<TSubject, TProperty, TValue> Of(
        int tick,
        IEnumerable<(TSubject subject, TProperty property, TValue value)> entries,
        IEnumerable<TSubject>? authSubjects = null)
    {
        var snapshot = new Snapshot<TSubject, TProperty, TValue>(tick);
        foreach (var (subject, property, value) in entries)
            snapshot.SetProperty(subject, property, value);
        if (authSubjects is not null)
            foreach (var subject in authSubjects)
                snapshot.SetAuth(subject, true);
        return snapshot;
    }

    public Snapshot<TSubject, TProperty, TValue> Duplicate()
    {
        var result = new Snapshot<TSubject, TProperty, TValue>(Tick);
        foreach (var (subject, props) in _data)
            result._data[subject] = new Dictionary<TProperty, TValue>(props);
        result._authSubjects.UnionWith(_authSubjects);
        return result;
    }

    public void SetAuth(TSubject subject, bool isAuth)
    {
        if (isAuth) _authSubjects.Add(subject);
        else _authSubjects.Remove(subject);
    }

    public void SetProperty(TSubject subject, TProperty property, TValue value)
    {
        if (!_data.TryGetValue(subject, out var props))
            _data[subject] = props = new Dictionary<TProperty, TValue>();
        props[property] = value;
    }

    public TValue? GetProperty(TSubject subject, TProperty property)
        => TryGetProperty(subject, property, out var value) ? value : default;

    public bool TryGetProperty(TSubject subject, TProperty property, out TValue value)
    {
        if (_data.TryGetValue(subject, out var props) && props.TryGetValue(property, out value!))
            return true;
        value = default!;
        return false;
    }

    public bool HasProperty(TSubject subject, TProperty property)
        => _data.TryGetValue(subject, out var props) && props.ContainsKey(property);

    /// <returns>True if anything changed.</returns>
    public bool Merge(Snapshot<TSubject, TProperty, TValue> snapshot)
    {
        var hasChanged = false;

        foreach (var (subject, theirProps) in snapshot._data)
        {
            if (!_data.TryGetValue(subject, out var ownProps))
            {
                // We have no data of the subject, copy all
                _data[subject] = new Dictionary<TProperty, TValue>(theirProps);
                SetAuth(subject, snapshot.IsAuth(subject));
                hasChanged = true;
                continue;
            }

            if (snapshot.IsAuth(subject) || !IsAuth(subject))
            {
                hasChanged = hasChanged || !PropsEqual(ownProps, theirProps);
                foreach (var (property, value) in theirProps)
                    ownProps[property] = value;
                SetAuth(subject, snapshot.IsAuth(subject));
            }
        }

        return hasChanged;
    }

    /// <summary>Removes every subject for which <paramref name="isValid"/> returns false.</summary>
    public void Sanitize(Func<TSubject, bool> isValid)
    {
        List<TSubject>? invalid = null;
        foreach (var subject in _data.Keys)
        {
            if (!isValid(subject))
            {
                invalid ??= new List<TSubject>();
                invalid.Add(subject);
            }
        }

        if (invalid is null) return;
        foreach (var subject in invalid)
            EraseSubject(subject);
    }

    public bool HasSubject(TSubject subject, bool requireAuth = false)
    {
        if (!_data.ContainsKey(subject)) return false;
        if (requireAuth && !IsAuth(subject)) return false;
        return true;
    }

    public bool HasSubjects(IEnumerable<TSubject> subjects, bool requireAuth = false)
    {
        foreach (var subject in subjects)
            if (!HasSubject(subject, requireAuth)) return false;
        return true;
    }

    public void EraseSubject(TSubject subject)
    {
        _data.Remove(subject);
        _authSubjects.Remove(subject);
    }

    /// <summary>Copies the data of <paramref name="subject"/> into <paramref name="target"/>. If there is no data, erases it from target too.</summary>
    public void CopySubjectTo(TSubject subject, Snapshot<TSubject, TProperty, TValue> target)
    {
        target.EraseSubject(subject);
        if (_data.TryGetValue(subject, out var props))
            target._data[subject] = new Dictionary<TProperty, TValue>(props);
    }

    public IReadOnlyCollection<TSubject> Subjects => _data.Keys;
    public IReadOnlyCollection<TSubject> AuthSubjects => _authSubjects;

    public IReadOnlyCollection<TProperty> GetSubjectProperties(TSubject subject)
        => _data.TryGetValue(subject, out var props) ? props.Keys : Array.Empty<TProperty>();

    public IReadOnlyDictionary<TProperty, TValue> GetSubjectData(TSubject subject)
        => _data.TryGetValue(subject, out var props) ? props : EmptyProps;

    private static readonly IReadOnlyDictionary<TProperty, TValue> EmptyProps = new Dictionary<TProperty, TValue>();

    public bool IsEmpty => _data.Count == 0;

    public int Size
    {
        get
        {
            var result = 0;
            foreach (var props in _data.Values) result += props.Count;
            return result;
        }
    }

    public bool IsAuth(TSubject subject) => _authSubjects.Contains(subject);

    public bool Equals(Snapshot<TSubject, TProperty, TValue>? other)
    {
        if (other is null) return false;
        if (Tick != other.Tick) return false;
        if (_data.Count != other._data.Count) return false;
        if (!_authSubjects.SetEquals(other._authSubjects)) return false;
        foreach (var (subject, props) in _data)
        {
            if (!other._data.TryGetValue(subject, out var otherProps)) return false;
            if (!PropsEqual(props, otherProps)) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as Snapshot<TSubject, TProperty, TValue>);

    public override int GetHashCode() => HashCode.Combine(Tick, _data.Count, _authSubjects.Count);

    public override string ToString()
    {
        var sb = new StringBuilder("Snapshot(#").Append(Tick);
        foreach (var (subject, props) in _data)
            foreach (var (property, value) in props)
                sb.Append(", ").Append(subject).Append(':').Append(property).Append('(').Append(IsAuth(subject)).Append("): ").Append(value);
        return sb.Append(')').ToString();
    }

    private static bool PropsEqual(Dictionary<TProperty, TValue> a, Dictionary<TProperty, TValue> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (property, value) in a)
        {
            if (!b.TryGetValue(property, out var otherValue)) return false;
            if (!ValueComparer.Equals(value, otherValue)) return false;
        }
        return true;
    }
}
