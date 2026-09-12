using Netfox.Core.Time;

namespace Netfox.Core.Tests.Time;

public class ClockSynchronizerTests
{
    private sealed class FakeTime
    {
        public double Now;
    }

    private static (ClockSynchronizer sync, FakeTime time) Make(double panicThreshold = 2.0)
    {
        var time = new FakeTime();
        var sync = new ClockSynchronizer(new SystemClock(() => time.Now))
        {
            AdjustSteps = 8,
            SyncSamples = 8,
            PanicThreshold = panicThreshold,
        };
        sync.Reset();
        return (sync, time);
    }

    /// <summary>Simulates a roundtrip against a remote clock that is <paramref name="remoteAhead"/> seconds ahead.</summary>
    private static ClockSample? Roundtrip(ClockSynchronizer sync, FakeTime time, double remoteAhead, double latency)
    {
        var idx = sync.BeginSample();
        var pingSent = sync.Time;
        time.Now += latency;
        var pingReceived = pingSent + latency + remoteAhead;
        var pongSent = pingReceived;
        time.Now += latency;
        return sync.CompleteSample(idx, pingReceived, pongSent);
    }

    [Fact]
    public void Sample_ShouldMeasureOffsetAndRtt()
    {
        var (sync, time) = Make();
        var sample = Roundtrip(sync, time, remoteAhead: 1.0, latency: 0.05)!;
        Assert.Equal(1.0, sample.Offset, 6);
        Assert.Equal(0.1, sample.Rtt, 6);
        Assert.Equal(0.1, sync.Rtt, 6);
        Assert.Equal(0.0, sync.RttJitter, 6);
    }

    [Fact]
    public void Discipline_ShouldNudgeByOffsetOverAdjustSteps()
    {
        var (sync, time) = Make();
        Roundtrip(sync, time, remoteAhead: 1.0, latency: 0.05);
        Assert.Equal(1.0 / 8.0, sync.Clock.Offset, 6);
        Assert.Equal(1.0 - 1.0 / 8.0, sync.RemoteOffset, 6);
    }

    [Fact]
    public void Discipline_ShouldConvergeOverSamples()
    {
        var (sync, time) = Make();
        for (var i = 0; i < 40; i++)
            Roundtrip(sync, time, remoteAhead: 1.0 - sync.Clock.Offset, latency: 0.05);
        Assert.Equal(1.0, sync.Clock.Offset, 2);
    }

    [Fact]
    public void Discipline_ShouldPanicAboveThreshold()
    {
        var (sync, time) = Make(panicThreshold: 0.5);
        double? panicked = null;
        sync.OnPanic += o => panicked = o;

        var inFlight = sync.BeginSample();
        Roundtrip(sync, time, remoteAhead: 1.0, latency: 0.05);

        Assert.NotNull(panicked);
        Assert.Equal(1.0, panicked!.Value, 6);
        Assert.Equal(1.0, sync.Clock.Offset, 6);
        Assert.Equal(0.0, sync.RemoteOffset);
        // In-flight samples are dropped on panic
        Assert.Null(sync.CompleteSample(inFlight, 0, 0));
    }

    [Fact]
    public void SyncInterval_ShouldClampToMinimum()
    {
        var (sync, _) = Make();
        sync.SyncInterval = 0.01;
        Assert.Equal(ClockSynchronizer.MinSyncInterval, sync.SyncInterval);
    }
}
