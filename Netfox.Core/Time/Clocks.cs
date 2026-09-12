using System.Diagnostics;

namespace Netfox.Core.Time;

/// <summary>Wall-clock time sources. Port of time/network-clocks.gd.</summary>
public static class Clocks
{
    private static readonly double UnixAtStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    private static readonly long StampAtStart = Stopwatch.GetTimestamp();

    /// <summary>Unix time in seconds with Stopwatch resolution. Equivalent of Time.get_unix_time_from_system().</summary>
    public static double UnixTime()
        => UnixAtStart + (Stopwatch.GetTimestamp() - StampAtStart) / (double)Stopwatch.Frequency;
}

/// <summary>Reference clock: raw wall time plus an adjustable offset.</summary>
public sealed class SystemClock
{
    private readonly Func<double> _rawTime;

    public double Offset { get; private set; }

    public SystemClock(Func<double>? rawTime = null)
    {
        _rawTime = rawTime ?? Clocks.UnixTime;
    }

    public double RawTime => _rawTime();
    public double Time => RawTime + Offset;

    public void Adjust(double offset) => Offset += offset;

    public void SetTime(double time) => Offset = time - RawTime;
}

/// <summary>Simulation clock: advanced manually by wall-clock deltas, optionally stretched.</summary>
public sealed class SteppingClock
{
    private readonly Func<double> _rawTime;
    private double _lastStep;

    public double Time { get; private set; }

    public SteppingClock(Func<double>? rawTime = null)
    {
        _rawTime = rawTime ?? Clocks.UnixTime;
        _lastStep = RawTime;
    }

    public double RawTime => _rawTime();

    public void Adjust(double offset) => Time += offset;

    public void SetTime(double time)
    {
        _lastStep = RawTime;
        Time = time;
    }

    public void Step(double multiplier = 1.0)
    {
        var current = RawTime;
        var duration = current - _lastStep;
        _lastStep = current;
        Adjust(duration * multiplier);
    }
}
