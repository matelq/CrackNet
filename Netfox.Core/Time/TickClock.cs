using Netfox.Core.Logging;

namespace Netfox.Core.Time;

/// <summary>
/// The arithmetic of the NetworkTime tick loop without any side effects: clock stretching towards a reference time,
/// stall detection, and how many ticks to run this frame. Port of _loop / _get_ticks_in_loop in network-time.gd.
/// </summary>
public sealed class TickClock
{
    private static readonly NetfoxLogger Logger = NetfoxLogger.ForNetfox("NetworkTime");

    private readonly SteppingClock _clock;
    private double _nextTickTime;
    private double _lastProcessTime;

    public int Tickrate { get; set; } = 30;
    public int MaxTicksPerFrame { get; set; } = 8;
    public double StallThreshold { get; set; } = 1.0;
    public double ClockStretchMax { get; set; } = 1.25;
    public bool SyncToPhysics { get; set; }

    public int Tick { get; set; }
    public double StretchFactor { get; private set; } = 1.0;

    /// <summary>Set to true when the game was paused; the next Advance() skips catch-up ticks and re-syncs the tick.</summary>
    public bool WasPaused { get; set; }

    public TickClock(Func<double>? rawTime = null)
    {
        _clock = new SteppingClock(rawTime);
    }

    public double Ticktime => 1.0 / Tickrate;
    public double Time => _clock.Time;

    /// <summary>0.0 right after a tick, 1.0 right before the next one.</summary>
    public double TickFactor => 1.0 - Math.Clamp((_nextTickTime - _lastProcessTime) * Tickrate, 0.0, 1.0);

    public double ClockOffset(double referenceTime) => referenceTime - _clock.Time;

    public double TicksToSeconds(int ticks) => ticks * Ticktime;
    public int SecondsToTicks(double seconds) => (int)(seconds * Tickrate);

    /// <summary>Aligns the simulation clock with the reference clock, e.g. after the initial sync.</summary>
    public void Reset(double referenceTime)
    {
        _clock.SetTime(referenceTime);
        _lastProcessTime = referenceTime;
        _nextTickTime = referenceTime;
    }

    /// <summary>Steps the simulation clock towards <paramref name="referenceTime"/> and returns how many ticks to run now (0 or more).</summary>
    public int Advance(double referenceTime)
    {
        _clock.Step(StretchFactor);
        var clockDiff = referenceTime - _clock.Time;

        // Ignore diffs under 1ms
        clockDiff = Math.Sign(clockDiff) * Math.Max(Math.Abs(clockDiff) - 0.001, 0.0);

        var stretchMin = 1.0 / ClockStretchMax;
        var stretchF = Math.Clamp(InverseLerp(-Ticktime, Ticktime, clockDiff), 0.0, 1.0);
        var previousStretch = StretchFactor;
        StretchFactor = Lerp(stretchMin, ClockStretchMax, stretchF);

        // Detect editor pause / stall
        var clockStep = _clock.Time - _lastProcessTime;
        var clockStepRaw = clockStep / previousStretch;
        if (clockStepRaw > StallThreshold)
        {
            WasPaused = true;
            Logger.Debug("Game stalled for {0:F4}s, assuming it was a pause", clockStepRaw);
        }

        if (WasPaused)
        {
            WasPaused = false;
            _nextTickTime += clockStep;
            Tick = SecondsToTicks(referenceTime);
        }

        _lastProcessTime = _clock.Time;
        return TicksInLoop();
    }

    /// <summary>Call after each simulated tick.</summary>
    public void CompleteTick()
    {
        Tick++;
        _nextTickTime += Ticktime;
    }

    private int TicksInLoop()
    {
        var debt = _lastProcessTime - _nextTickTime;

        if (SyncToPhysics)
        {
            if (debt > Ticktime) return Math.Min(1 + (int)(debt / Ticktime), MaxTicksPerFrame);
            if (debt < -Ticktime) return 0;
            return 1;
        }

        return Math.Clamp((int)Math.Ceiling(debt / Ticktime), 0, MaxTicksPerFrame);
    }

    private static double InverseLerp(double from, double to, double value) => (value - from) / (to - from);
    private static double Lerp(double from, double to, double weight) => from + (to - from) * weight;
}
