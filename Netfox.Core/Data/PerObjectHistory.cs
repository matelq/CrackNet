using Netfox.Core.Collections;

namespace Netfox.Core.Data;

/// <summary>Per-object timeline of ObjectSnapshots. Port of servers/data/per-object-history.gd.</summary>
public sealed class PerObjectHistory<TSubject, TProperty, TValue>
    where TSubject : class
    where TProperty : notnull
{
    private readonly int _historySize;
    private readonly Dictionary<TSubject, HistoryBuffer<ObjectSnapshot<TSubject, TProperty, TValue>>> _data
        = new(ReferenceEqualityComparer.Instance);

    public PerObjectHistory(int historySize)
    {
        _historySize = historySize;
    }

    public IReadOnlyCollection<TSubject> Subjects => _data.Keys;

    public bool IsAuth(int tick, TSubject subject)
    {
        if (!_data.TryGetValue(subject, out var history)) return false;
        return history.TryGetAt(tick, out var snapshot) && snapshot.IsAuth;
    }

    public void EraseSubject(TSubject subject) => _data.Remove(subject);

    /// <returns>The snapshot at <paramref name="tick"/>, or null if the tick is too old to store.</returns>
    public ObjectSnapshot<TSubject, TProperty, TValue>? EnsureSnapshot(int tick, TSubject subject, bool carryForward)
    {
        var history = HistoryOf(subject);

        if (!history.HasAt(tick))
        {
            if (carryForward && history.TryGetLatestAt(tick, out var latest))
                history.SetAt(tick, latest.Duplicate());
            else
                history.SetAt(tick, new ObjectSnapshot<TSubject, TProperty, TValue>(subject));
        }

        return history.GetAt(tick);
    }

    public ObjectSnapshot<TSubject, TProperty, TValue>? GetSnapshot(int tick, TSubject subject)
        => _data.TryGetValue(subject, out var history) ? history.GetAt(tick) : null;

    public ObjectSnapshot<TSubject, TProperty, TValue>? GetLatestSnapshot(int tick, TSubject subject)
    {
        if (!_data.TryGetValue(subject, out var history)) return null;
        return history.TryGetLatestAt(tick, out var snapshot) ? snapshot : null;
    }

    /// <returns>Latest known tick at or before <paramref name="tick"/>, or -1.</returns>
    public int GetLatestTick(int tick, TSubject subject)
    {
        if (!_data.TryGetValue(subject, out var history)) return -1;
        return history.GetLatestIndexAt(tick);
    }

    public void SetProperty(int tick, TSubject subject, TProperty property, TValue value)
    {
        var history = HistoryOf(subject);
        if (!history.HasAt(tick))
            history.SetAt(tick, new ObjectSnapshot<TSubject, TProperty, TValue>(subject));
        history.GetAt(tick)?.SetValue(property, value);
    }

    public bool HasProperty(int tick, TSubject subject, TProperty property)
        => GetSnapshot(tick, subject)?.HasValue(property) ?? false;

    public TValue? GetProperty(int tick, TSubject subject, TProperty property, TValue? fallback = default)
    {
        var snapshot = GetSnapshot(tick, subject);
        return snapshot is not null && snapshot.TryGetValue(property, out var value) ? value : fallback;
    }

    private HistoryBuffer<ObjectSnapshot<TSubject, TProperty, TValue>> HistoryOf(TSubject subject)
    {
        if (!_data.TryGetValue(subject, out var history))
            _data[subject] = history = new HistoryBuffer<ObjectSnapshot<TSubject, TProperty, TValue>>(_historySize);
        return history;
    }
}
