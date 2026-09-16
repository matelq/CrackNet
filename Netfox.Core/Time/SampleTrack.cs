namespace Netfox.Core.Time;

/// <summary>
/// Tick-stamped samples of one object, read at a display tick from its peer's <see cref="PlaybackClock"/>. Holds the
/// newest sample once the display tick passes it. Values must be immutable after insertion.
/// </summary>
public sealed class SampleTrack<T>
{
    private readonly SortedList<int, T> _samples = new();
    private readonly int _capacity;

    public SampleTrack(int capacity = 64)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count => _samples.Count;

    /// <summary>
    /// Adds a sample unless it lands before <paramref name="shownTick"/>: a late packet inside the interval already on
    /// screen would bend the viewer's past. Returns whether it was kept.
    /// </summary>
    public bool Push(int tick, T value, double? shownTick)
    {
        // The first sample is the object's beginning, even if its peer's already-running shared clock passed it. The
        // caller gives that object a private catch-up cursor; once there is history, the usual no-rewritten-past rule
        // applies again.
        if (_samples.Count > 0 && shownTick is { } shown && tick < shown) return false;
        _samples[tick] = value;
        while (_samples.Count > _capacity) _samples.RemoveAt(0);
        return true;
    }

    /// <summary>
    /// The samples around <paramref name="tick"/> and how far between them it is; false before the first sample, and
    /// both the newest after the last. Drops samples that no later tick can need.
    /// </summary>
    public bool TrySample(double tick, out T from, out T to, out double fraction)
    {
        from = to = default!;
        fraction = 0;
        if (_samples.Count == 0) return false;

        // Before the first sample there is nothing to show yet: showing that sample early would put this object ahead
        // of everything else its peer sends
        var keys = _samples.Keys;
        if (tick < keys[0]) return false;
        if (tick == keys[0])
        {
            from = to = _samples.Values[0];
            return true;
        }

        var next = 1;
        while (next < keys.Count && keys[next] < tick) next++;
        if (next == keys.Count)
        {
            from = to = _samples.Values[^1];
            while (_samples.Count > 1) _samples.RemoveAt(0);
            return true;
        }

        while (next > 1)
        {
            _samples.RemoveAt(0);
            next--;
        }
        from = _samples.Values[0];
        to = _samples.Values[1];
        fraction = (tick - keys[0]) / (keys[1] - keys[0]);
        return true;
    }

    /// <summary>The newest sample, if any.</summary>
    public bool TryGetNewest(out int tick, out T value)
    {
        tick = 0;
        value = default!;
        if (_samples.Count == 0) return false;
        tick = _samples.Keys[^1];
        value = _samples.Values[^1];
        return true;
    }

    public void Clear() => _samples.Clear();
}
