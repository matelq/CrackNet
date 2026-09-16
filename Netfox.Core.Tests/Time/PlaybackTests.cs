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
    public void JitterAndLossKeepMotionMonotonicAndMemoryBounded()
    {
        var clock = new PlaybackClock(delayTicks: 4);
        var track = new SampleTrack<double>(capacity: 32);
        var random = new Random(42);
        var deliveries = Enumerable.Range(0, 300).Where(t => t % 50 < 42 && random.NextDouble() > .1)
            .Select(t => (Tick: t, Arrival: t + random.Next(0, 5))).OrderBy(p => p.Arrival).ToList();

        var received = 0;
        var previous = double.NegativeInfinity;
        var biggestStep = 0d;
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
            if (double.IsFinite(previous)) biggestStep = Math.Max(biggestStep, displayed - previous);
            Assert.InRange(track.Count, 1, 32);
            previous = displayed;
        }

        Assert.True(previous > 290);
        Assert.True(clock.Holds > 0);
        // Half a tick per frame at up to 5% fast; anything bigger is a jump the viewer sees
        Assert.InRange(biggestStep, 0, .526);
    }

    [Fact]
    public void FarBehindJumpsForwardInsteadOfCrawling()
    {
        var clock = new PlaybackClock(delayTicks: 2, resyncTicks: 10);
        clock.Observe(0);
        clock.Observe(100);
        Assert.Equal(98d, clock.Tick);
    }
}
