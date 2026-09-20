using CrackNet.Core.Time;

namespace CrackNet.Core.Tests.Time;

public class TickClockTests
{
    private sealed class FakeTime
    {
        public double Now;
    }

    private static (TickClock clock, FakeTime time) Make()
    {
        var time = new FakeTime();
        var clock = new TickClock(() => time.Now) { Tickrate = 10, MaxTicksPerFrame = 8, StallThreshold = 1.0, ClockStretchMax = 1.25 };
        clock.Reset(0.0);
        return (clock, time);
    }

    // netfox quirk: stretch min is 1/max, so a zero diff maps to lerp(0.8, 1.25, 0.5) rather than 1.0
    private const double NeutralStretch = 0.8 + (1.25 - 0.8) * 0.5;

    [Fact]
    public void Advance_ShouldRunOneTickPerTicktimeElapsed()
    {
        var (clock, time) = Make();
        time.Now = 0.25;
        var ticks = clock.Advance(referenceTime: 0.25);
        Assert.Equal(3, ticks); // schedules at 0.0, 0.1, 0.2
        for (var i = 0; i < ticks; i++) clock.CompleteTick();
        Assert.Equal(3, clock.Tick);
        Assert.Equal(0.5, clock.TickFactor, 6); // halfway to the tick scheduled at 0.3
    }

    [Fact]
    public void Advance_ShouldCapTicksPerFrame()
    {
        var (clock, time) = Make();
        time.Now = 0.95; // 9.5 ticks of debt, under the stall threshold
        Assert.Equal(8, clock.Advance(0.95));
    }

    [Fact]
    public void Advance_ShouldSpeedUpWhenBehindReference()
    {
        var (clock, time) = Make();
        time.Now = 0.01;
        clock.Advance(referenceTime: 0.5);
        Assert.Equal(1.25, clock.StretchFactor, 6);
    }

    [Fact]
    public void Advance_ShouldSlowDownWhenAheadOfReference()
    {
        var (clock, time) = Make();
        time.Now = 0.01;
        clock.Advance(referenceTime: -0.5);
        Assert.Equal(1.0 / 1.25, clock.StretchFactor, 6);
    }

    [Fact]
    public void Advance_ShouldIgnoreSubMillisecondDrift()
    {
        var (clock, time) = Make();
        time.Now = 0.01;
        clock.Advance(referenceTime: 0.0105);
        Assert.Equal(NeutralStretch, clock.StretchFactor, 6);
    }

    [Fact]
    public void Advance_ShouldTreatLongGapAsPauseAndResync()
    {
        var (clock, time) = Make();
        time.Now = 5.0;
        var ticks = clock.Advance(referenceTime: 12.0);
        Assert.Equal(1, ticks); // the step's own tick, not the seven seconds the pause owes
        Assert.Equal(120, clock.Tick);
    }

    [Fact]
    public void Advance_ShouldRunOneTickPerCallUntilAhead()
    {
        var (clock, time) = Make();
        time.Now = 0.001;
        Assert.Equal(1, clock.Advance(0.001));
        clock.CompleteTick();
        time.Now = 0.002;
        Assert.Equal(1, clock.Advance(0.002)); // less than a ticktime ahead: still one tick per physics frame
        clock.CompleteTick();
        time.Now = 0.003;
        Assert.Equal(0, clock.Advance(0.003)); // now more than a ticktime ahead of schedule
    }
}
