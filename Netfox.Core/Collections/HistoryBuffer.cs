namespace Netfox.Core.Collections;

/// <summary>
/// Maps ticks to arbitrary data, stored in a sliding ring buffer.
/// Port of netfox.internals/history-buffer.gd.
/// </summary>
public sealed class HistoryBuffer<T>
{
    private const int Unset = int.MinValue;

    private readonly int _capacity;
    private T?[] _buffer;
    private int[] _previous;

    private int _tail;
    private int _head;

    public HistoryBuffer(int capacity = 64)
    {
        _capacity = capacity;
        _buffer = new T?[capacity];
        _previous = new int[capacity];
        Array.Fill(_previous, Unset);
    }

    public static HistoryBuffer<T> Of(int capacity, IEnumerable<KeyValuePair<int, T>> data)
    {
        var result = new HistoryBuffer<T>(capacity);
        foreach (var (idx, value) in data)
            result.SetAt(idx, value);
        return result;
    }

    public HistoryBuffer<T> Duplicate()
    {
        return new HistoryBuffer<T>(_capacity)
        {
            _buffer = (T?[])_buffer.Clone(),
            _previous = (int[])_previous.Clone(),
            _tail = _tail,
            _head = _head,
        };
    }

    public int Size => _head - _tail;
    public int Capacity => _capacity;
    public bool IsEmpty => Size == 0;
    public bool IsNotEmpty => Size != 0;
    public int EarliestIndex => _tail;
    public int LatestIndex => _head - 1;

    public void Push(T value)
    {
        var slot = Slot(_head);
        _buffer[slot] = value;
        _previous[slot] = _head;
        _head++;
        _tail += Math.Max(0, Size - _capacity);
    }

    public T Pop()
    {
        if (IsEmpty) throw new InvalidOperationException("History buffer is empty!");
        var value = _buffer[Slot(_tail)]!;
        _tail++;
        return value;
    }

    public List<T> Values()
    {
        var result = new List<T>();
        for (var i = _tail; i <= _head; i++)
        {
            if (_previous[Slot(i)] == i)
                result.Add(_buffer[Slot(i)]!);
        }
        return result;
    }

    public void SetAt(int at, T value)
    {
        if (IsEmpty)
        {
            // Buffer is empty, jump to specified index
            _tail = at;
            _head = at;
            Push(value);
        }
        else if (at < _head - _capacity)
        {
            // Would wrap back around and overwrite current data
        }
        else if (at == _head)
        {
            Push(value);
        }
        else if (at < _head)
        {
            _buffer[Slot(at)] = value;
            for (var i = at; i < _head; i++)
            {
                if (_previous[Slot(i)] == i) break;
                _previous[Slot(i)] = at;
            }
            _tail = Math.Min(_tail, at);
        }
        else if (at >= _head + _capacity)
        {
            // Leaving all data behind
            _tail = at;
            _head = at;
            Array.Fill(_previous, Unset);
            Array.Fill(_buffer, default);
            Push(value);
        }
        else
        {
            // Skipping forward a bit
            var previous = _head - 1;
            while (_head < at)
            {
                _previous[Slot(_head)] = previous;
                _head++;
            }
            _tail += Math.Max(0, Size - _capacity);
            Push(value);
        }
    }

    public bool HasAt(int at)
    {
        if (IsEmpty) return false;
        if (at < _head - _capacity) return false;
        if (at >= _head) return false;
        return _previous[Slot(at)] == at;
    }

    public T? GetAt(int at, T? fallback = default)
        => HasAt(at) ? _buffer[Slot(at)] : fallback;

    public bool TryGetAt(int at, out T value)
    {
        if (HasAt(at))
        {
            value = _buffer[Slot(at)]!;
            return true;
        }
        value = default!;
        return false;
    }

    public bool HasLatestAt(int at)
    {
        if (IsEmpty) return false;
        return at >= _tail;
    }

    /// <returns>Index of the latest entry at or before <paramref name="at"/>, or -1.</returns>
    public int GetLatestIndexAt(int at)
    {
        if (!HasLatestAt(at)) return -1;
        if (at >= _head) return LatestIndex;
        return _previous[Slot(at)];
    }

    public T? GetLatestAt(int at) => GetAt(GetLatestIndexAt(at));

    public bool TryGetLatestAt(int at, out T value) => TryGetAt(GetLatestIndexAt(at), out value);

    public void Clear() => _tail = _head;

    private int Slot(int index)
    {
        var m = index % _capacity;
        return m < 0 ? m + _capacity : m;
    }
}
