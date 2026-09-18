namespace CrackNet.Core.Time;

/// <summary>
/// A newly seen object's display position. It begins at that object's first sample even when its peer's shared clock
/// is already ahead, then runs faster until it can rejoin the shared clock without skipping its opening motion.
/// </summary>
public sealed class ObjectPlaybackCursor
{
    private readonly double _catchUpRate;
    private double? _tick;

    public ObjectPlaybackCursor(double catchUpRate = 2)
    {
        if (!double.IsFinite(catchUpRate) || catchUpRate <= 1)
            throw new ArgumentOutOfRangeException(nameof(catchUpRate));
        _catchUpRate = catchUpRate;
    }

    /// <summary>Whether this object is still behind the peer's shared display clock.</summary>
    public bool IsCatchingUp { get; private set; }

    /// <summary>Starts at the first sample, or at the shared clock when that sample is not in its past.</summary>
    public double Start(int firstTick, double sharedTick)
    {
        _tick = Math.Min(firstTick, sharedTick);
        IsCatchingUp = _tick < sharedTick;
        return _tick.Value;
    }

    /// <summary>Advances the private opening timeline, returning to the shared tick as soon as it reaches it.</summary>
    public double Advance(double sharedTick, double elapsedTicks)
    {
        if (!double.IsFinite(sharedTick)) throw new ArgumentOutOfRangeException(nameof(sharedTick));
        if (!double.IsFinite(elapsedTicks) || elapsedTicks < 0) throw new ArgumentOutOfRangeException(nameof(elapsedTicks));
        if (_tick is not { } shown || !IsCatchingUp) return sharedTick;

        shown += elapsedTicks * _catchUpRate;
        if (shown >= sharedTick)
        {
            IsCatchingUp = false;
            _tick = sharedTick;
        }
        else
        {
            _tick = shown;
        }
        return _tick.Value;
    }

    public void Reset()
    {
        _tick = null;
        IsCatchingUp = false;
    }
}
