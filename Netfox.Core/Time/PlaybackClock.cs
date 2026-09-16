namespace Netfox.Core.Time;

/// <summary>
/// The display tick for everything one remote peer sends. One clock per peer, not per object, so a stack of crates or
/// a character and what it holds are always shown at the same moment.
/// <para>
/// The clock trails the newest tick heard from the peer by a fixed delay and slews its rate to hold that depth, rather
/// than jumping when packet spacing changes. It never runs past the newest tick: when data stops it holds there and the
/// depth rebuilds by running slow, so an underrun costs a moment of standing still, never a freeze to rebuffer. It
/// never runs backwards.
/// </para>
/// </summary>
public sealed class PlaybackClock
{
    private const double Slew = 0.05;
    private readonly double _delay;
    private readonly double _resyncDepth;

    /// <param name="delayTicks">How far behind the newest tick the clock aims to run.</param>
    /// <param name="resyncTicks">
    /// How far behind the target the clock may fall before it jumps forward instead of slewing: after a long stall,
    /// catching up at 5% faster would take minutes.
    /// </param>
    public PlaybackClock(double delayTicks, double resyncTicks = 30)
    {
        if (!double.IsFinite(delayTicks) || delayTicks < 0) throw new ArgumentOutOfRangeException(nameof(delayTicks));
        _delay = delayTicks;
        _resyncDepth = delayTicks + resyncTicks;
    }

    /// <summary>The tick to display, or null until the peer has sent anything.</summary>
    public double? Tick { get; private set; }

    /// <summary>The newest tick heard from the peer.</summary>
    public int Newest { get; private set; }

    /// <summary>Advances where the clock started holding at the newest tick; a measure of underruns.</summary>
    public int Holds { get; private set; }

    /// <summary>Tells the clock a sample for <paramref name="tick"/> arrived from the peer.</summary>
    public void Observe(int tick)
    {
        if (Tick is not { } shown)
        {
            Newest = tick;
            Tick = tick - _delay;
            return;
        }

        if (tick <= Newest) return;
        Newest = tick;
        if (Newest - shown > _resyncDepth) Tick = Newest - _delay;
    }

    /// <summary>Moves the display tick on by <paramref name="elapsedTicks"/> of local time.</summary>
    public void Advance(double elapsedTicks)
    {
        if (!double.IsFinite(elapsedTicks) || elapsedTicks < 0) throw new ArgumentOutOfRangeException(nameof(elapsedTicks));
        if (Tick is not { } shown) return;

        var behind = Newest - _delay - shown;
        var rate = behind > 1 ? 1 + Slew : behind < -1 ? 1 - Slew : 1;
        var next = shown + elapsedTicks * rate;

        if (next >= Newest)
        {
            if (shown < Newest) Holds++;
            next = Math.Max(shown, Newest);
        }
        Tick = next;
    }

    public void Reset()
    {
        Tick = null;
        Newest = 0;
        Holds = 0;
    }
}
