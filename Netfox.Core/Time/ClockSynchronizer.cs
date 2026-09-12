using Netfox.Core.Logging;

namespace Netfox.Core.Time;

/// <summary>
/// Transport-free core of NetworkTimeSynchronizer: owns the reference clock, in-flight and completed samples,
/// and disciplines the clock after every completed sample. Netfox.Godot drives it with ping/pong commands and a timer.
/// Port of the algorithm in network-time-synchronizer.gd.
/// </summary>
public sealed class ClockSynchronizer
{
    public const double MinSyncInterval = 0.1;

    private static readonly NetfoxLogger Logger = NetfoxLogger.ForNetfox("NetworkTimeSynchronizer");

    private readonly Queue<ClockSample> _samples = new();
    private readonly Dictionary<int, ClockSample> _awaiting = new();
    private int _sampleIdx;
    private double _syncInterval = 0.25;

    public SystemClock Clock { get; }

    /// <summary>Seconds between samples, never below MinSyncInterval.</summary>
    public double SyncInterval
    {
        get => Math.Max(_syncInterval, MinSyncInterval);
        set => _syncInterval = value;
    }

    public int SyncSamples { get; set; } = 8;
    public int AdjustSteps { get; set; } = 8;
    public double PanicThreshold { get; set; } = 2.0;

    public double Rtt { get; private set; }
    public double RttJitter { get; private set; }
    /// <summary>Estimated remaining offset to the remote clock after the last nudge.</summary>
    public double RemoteOffset { get; private set; }

    /// <summary>Emitted with the offending offset when the clock is hard-reset.</summary>
    public event Action<double>? OnPanic;

    public ClockSynchronizer(SystemClock? clock = null)
    {
        Clock = clock ?? new SystemClock();
    }

    public double Time => Clock.Time;

    /// <summary>Resets clock and sample state for a new sync session.</summary>
    public void Reset()
    {
        Clock.SetTime(0.0);
        _sampleIdx = 0;
        _samples.Clear();
        _awaiting.Clear();
        Rtt = RttJitter = RemoteOffset = 0.0;
    }

    /// <summary>Records ping_sent for a new sample and returns its index, to be echoed back in the pong.</summary>
    public int BeginSample()
    {
        var sample = new ClockSample { PingSent = Clock.Time };
        _awaiting[_sampleIdx] = sample;
        return _sampleIdx++;
    }

    /// <summary>Completes an in-flight sample and disciplines the clock. Returns null if the sample was dropped by a panic.</summary>
    public ClockSample? CompleteSample(int idx, double pingReceived, double pongSent)
    {
        var pongReceived = Clock.Time;
        if (!_awaiting.Remove(idx, out var sample))
            return null;

        sample.PingReceived = pingReceived;
        sample.PongSent = pongSent;
        sample.PongReceived = pongReceived;
        Logger.Trace("Received sample: {0}", sample);

        _samples.Enqueue(sample);
        while (_samples.Count > Math.Max(SyncSamples, 1))
            _samples.Dequeue();

        Discipline();
        return sample;
    }

    private void Discipline()
    {
        if (_samples.Count == 0)
        {
            Logger.Warning("Trying to discipline the clock with no samples available!");
            return;
        }

        var sorted = _samples.OrderBy(s => s.Rtt).ToList();

        var rttMin = sorted[0].Rtt;
        var rttMax = sorted[^1].Rtt;
        Rtt = (rttMax + rttMin) / 2.0;
        RttJitter = (rttMax - rttMin) / 2.0;

        var offset = 0.0;
        var weightSum = 0.0;
        foreach (var sample in sorted)
        {
            var w = Math.Log(1.0 + sample.Rtt);
            offset += sample.Offset * w;
            weightSum += w;
        }

        if (Math.Abs(weightSum) > 1e-9)
            offset /= weightSum;
        else
            offset /= sorted.Count; // RTT is basically zero, fall back to a plain average

        if (Math.Abs(offset) > PanicThreshold)
        {
            Clock.Adjust(offset);
            _samples.Clear();
            _awaiting.Clear();
            RemoteOffset = 0.0;
            Logger.Warning("Offset {0}s is above panic threshold {1}s! Resetting clock", offset, PanicThreshold);
            OnPanic?.Invoke(offset);
        }
        else
        {
            var nudge = offset / AdjustSteps;
            Clock.Adjust(nudge);
            Logger.Trace("Adjusted clock by {0:F2}ms, offset: {1:F2}ms, new time: {2:F4}s", nudge * 1000.0, offset * 1000.0, Clock.Time);
            RemoteOffset = offset - nudge;
        }
    }
}
