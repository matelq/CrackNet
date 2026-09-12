namespace Netfox.Core.Collections;

/// <summary>Returns true on every nth IsNow() call. Port of netfox.internals/interval-scheduler.gd.</summary>
public sealed class IntervalScheduler
{
    private int _idx;

    public int Interval { get; set; }

    public IntervalScheduler(int interval = 1)
    {
        Interval = interval;
    }

    public bool IsNow()
    {
        if (Interval <= 0) return false;
        if (Interval == 1) return true;
        if (_idx + 1 >= Interval)
        {
            _idx = 0;
            return true;
        }
        _idx++;
        return false;
    }
}
