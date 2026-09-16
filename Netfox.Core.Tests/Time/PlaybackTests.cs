using Netfox.Core.Time;

namespace Netfox.Core.Tests.Time;

public class PlaybackTests
{
    private static double Sample(SampleTrack<double> track, double tick)
    {
        Assert.True(track.TrySample(tick, out var from, out var to, out var fraction));
        return from + (to - from) * fraction;
    }

    [Fact]
    public void ObjectsFromOnePeerShareTheDisplayTick()
    {
        var clock = new PlaybackClock(delayTicks: 2);
        var crate = new SampleTrack<double>();
        var stacked = new SampleTrack<double>();

        for (var tick = 0; tick <= 10; tick++)
        {
            clock.Observe(tick);
            crate.Push(tick, tick, clock.Tick);
            stacked.Push(tick, tick + 100, clock.Tick);
            clock.Advance(1);
        }

        // Same clock, same moment: the stacked crate is exactly 100 above the one below it, never a tick off
        var shown = clock.Tick!.Value;
        Assert.Equal(Sample(crate, shown) + 100, Sample(stacked, shown), 8);
    }

    [Fact]
    public void UnderrunHoldsWithoutFreezingOrRewinding()
    {
        var clock = new PlaybackClock(delayTicks: 3);
        var track = new SampleTrack<double>();
        for (var tick = 0; tick <= 10; tick++)
        {
            clock.Observe(tick);
            track.Push(tick, tick, clock.Tick);
            clock.Advance(1);
        }

        // Burst loss: nothing for 8 ticks. The clock reaches the newest sample and holds there
        for (var i = 0; i < 8; i++) clock.Advance(1);
        Assert.Equal(10d, clock.Tick);
        Assert.Equal(10d, Sample(track, clock.Tick!.Value));
        Assert.True(clock.Holds > 0);

        // Data returns: motion resumes on the very next frame instead of waiting for the delay to refill
        var before = clock.Tick!.Value;
        for (var tick = 19; tick <= 20; tick++)
        {
            clock.Observe(tick);
            track.Push(tick, tick, clock.Tick);
        }
        clock.Advance(1);
        Assert.True(clock.Tick > before);
    }

    [Fact]
    public void LatePacketDoesNotRewriteTheShownPast()
    {
        var clock = new PlaybackClock(delayTicks: 2);
        var track = new SampleTrack<double>();
        foreach (var tick in new[] { 10, 14 })
        {
            clock.Observe(tick);
            track.Push(tick, tick, clock.Tick);
        }
        clock.Advance(4); // shown: 12, halfway between 10 and 14
        var shown = clock.Tick!.Value;

        Assert.False(track.Push(11, -100, clock.Tick));
        Assert.Equal(shown, Sample(track, shown), 8);
        Assert.True(track.Push(13, 13, clock.Tick));
    }

    [Fact]
    public void ANewTrackKeepsItsFirstSampleEvenWhenThePeerClockPassedIt()
    {
        var track = new SampleTrack<double>();

        Assert.True(track.Push(10, 10, shownTick: 50));
        Assert.False(track.Push(9, 9, shownTick: 10));
        Assert.Equal(10, Sample(track, 10));
    }

    [Fact]
    public void ANewObjectPlaysItsOwnStartThenCatchesTheSharedClock()
    {
        var cursor = new ObjectPlaybackCursor(catchUpRate: 2);

        Assert.Equal(10, cursor.Start(firstTick: 10, sharedTick: 20));
        Assert.Equal(12, cursor.Advance(sharedTick: 21, elapsedTicks: 1));
        Assert.Equal(14, cursor.Advance(sharedTick: 22, elapsedTicks: 1));
        for (var shared = 23; shared <= 32; shared++) cursor.Advance(shared, elapsedTicks: 1);

        Assert.False(cursor.IsCatchingUp);
        Assert.Equal(32, cursor.Advance(sharedTick: 32, elapsedTicks: 0));
    }

    [Fact]
    public void JitterAndLossKeepMotionMonotonicAndLatencyBounded()
    {
        var clock = new PlaybackClock(delayTicks: 4);
        var track = new SampleTrack<double>(capacity: 32);
        var random = new Random(42);
        // Eight ticks lost out of every fifty, 10% loss on top, up to five ticks of jitter
        var deliveries = Enumerable.Range(0, 300).Where(t => t % 50 < 42 && random.NextDouble() > .1)
            .Select(t => (Tick: t, Arrival: t + random.Next(0, 5))).OrderBy(p => p.Arrival).ToList();

        var received = 0;
        var previous = double.NegativeInfinity;
        var jumps = 0;
        var worstDepth = 0d;
        for (var frame = 0; frame < 700; frame++)
        {
            while (received < deliveries.Count && deliveries[received].Arrival <= frame / 2.0)
            {
                var tick = deliveries[received++].Tick;
                clock.Observe(tick);
                track.Push(tick, tick, clock.Tick);
            }
            clock.Advance(.5);
            if (clock.Tick is not { } shown || !track.TrySample(shown, out var a, out var b, out var f)) continue;

            var displayed = a + (b - a) * f;
            Assert.True(displayed >= previous);
            // Catching up plays up to half again as fast (0.75 of a tick per half-tick frame); only more is a skip
            if (double.IsFinite(previous) && displayed - previous > 2) jumps++;
            if (frame > 40) worstDepth = Math.Max(worstDepth, clock.Newest - shown);
            Assert.InRange(track.Count, 1, 32);
            previous = displayed;
        }

        Assert.True(previous > 280);
        Assert.True(clock.Holds > 0);
        // A skip forward only where a burst ended, never a creeping delay that takes seconds to drain
        Assert.InRange(jumps, 1, 6);
        Assert.InRange(worstDepth, 0, 4 + 5 + 8 + 1);
    }

    [Fact]
    public void MotionAfterARestShowsAtTheNormalDepthAtOnce()
    {
        var clock = new PlaybackClock(delayTicks: 3);
        // A resting peer sends a heartbeat every 30 ticks, then starts moving and sends every 2
        var ticks = Enumerable.Range(0, 6).Select(i => i * 30).Concat(Enumerable.Range(76, 20).Select(i => i * 2)).ToList();

        var next = 0;
        for (var now = 0.0; now <= 190; now += 0.5)
        {
            while (next < ticks.Count && ticks[next] <= now) clock.Observe(ticks[next++]);
            clock.Advance(.5);
        }

        // Moving since tick 152, data every 2 ticks: the display trails by the delay, not by a heartbeat interval
        Assert.InRange(clock.Newest - clock.Tick!.Value, 0, 3 + 2 + 1);
    }

    [Fact]
    public void FarBehindJumpsForwardInsteadOfCrawling()
    {
        var clock = new PlaybackClock(delayTicks: 2, resyncTicks: 10);
        clock.Observe(0);
        clock.Observe(100);
        Assert.Equal(98d, clock.Tick);
    }

    [Fact]
    public void AClockThatStartedLateCatchesUpWithAPeerThatOnlySendsHeartbeats()
    {
        // The first state arrives late (25 ticks), then the peer rests and sends a heartbeat every 30 ticks. Measured
        // against the last heartbeat the clock believed itself ahead most of the time and stayed ~25 ticks behind.
        var clock = new PlaybackClock(delayTicks: 3);
        var now = 25.0;
        clock.Observe(0);
        var nextHeartbeat = 30;
        for (; now < 25 + 150; now += 0.5)
        {
            clock.Advance(.5);
            if (now >= nextHeartbeat)
            {
                clock.Observe(nextHeartbeat);
                nextHeartbeat += 30;
            }
        }

        var behindRealTime = now - clock.Time!.Value;
        Assert.InRange(behindRealTime, 0, 3 + 2);
    }
}
