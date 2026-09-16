namespace Netfox.Core.Time;

/// <summary>
/// The display tick for everything one remote peer sends. One clock per peer, not per object, so a stack of crates or
/// a character and what it holds are always shown at the same moment.
/// <para>
/// The clock trails the newest tick heard from the peer by a fixed delay and slews its rate to hold that depth, rather
/// than jumping when packet spacing changes. The display never runs past the newest tick, but the clock's own time
/// keeps going while nothing arrives: a resting peer sends only heartbeats, and motion after a rest must show at the
/// normal depth at once, not a heartbeat late. After an outage that means a skip forward to where the data is, as
/// Source does, rather than seconds of added delay draining at a few percent. It never runs backwards.
/// </para>
/// </summary>
public sealed class PlaybackClock
{
    private const double Slew = 0.05;
    private readonly double _delay;
    private readonly double _resyncDepth;
    private readonly double _maxLead;
    private double _time;

    /// <param name="delayTicks">How far behind the newest tick the clock aims to run.</param>
    /// <param name="resyncTicks">
    /// How far behind the target the clock may fall before it jumps forward instead of slewing: after a long stall,
    /// catching up at 5% faster would take minutes.
    /// </param>
    /// <param name="maxLeadTicks">
    /// How far the clock's time may run past the newest tick while nothing arrives: at least the gap between heartbeats
    /// of a resting peer, so motion after a rest shows at the normal depth at once.
    /// </param>
    public PlaybackClock(double delayTicks, double resyncTicks = 30, double maxLeadTicks = 40)
    {
        if (!double.IsFinite(delayTicks) || delayTicks < 0) throw new ArgumentOutOfRangeException(nameof(delayTicks));
        _delay = delayTicks;
        _resyncDepth = delayTicks + resyncTicks;
        _maxLead = maxLeadTicks;
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
        if (Tick is null)
        {
            Newest = tick;
            _time = tick - _delay;
            Tick = _time;
            return;
        }

        if (tick <= Newest) return;
        Newest = tick;
        if (Newest - _delay - _time > _resyncDepth) _time = Newest - _delay;
        Tick = Math.Max(Tick.Value, Math.Min(_time, Newest));
    }

    /// <summary>Moves the display tick on by <paramref name="elapsedTicks"/> of local time.</summary>
    public void Advance(double elapsedTicks)
    {
        if (!double.IsFinite(elapsedTicks) || elapsedTicks < 0) throw new ArgumentOutOfRangeException(nameof(elapsedTicks));
        if (Tick is not { } shown) return;

        // Time keeps running while nothing arrives: a peer whose objects rest sends only heartbeats, and a clock that
        // stopped at the last one would be a heartbeat behind when motion resumed, catching up at 5% for seconds.
        // Capped, so a long outage does not leave it running into a future nothing will fill.
        var behind = Newest - _delay - _time;
        // Slowing down only makes sense while data flows; in silence being "ahead" of a stale newest tick is expected
        var rate = behind > 1 ? 1 + Slew : behind < -1 && _time < Newest ? 1 - Slew : 1;
        _time = Math.Min(_time + elapsedTicks * rate, Newest + _maxLead);

        var next = Math.Min(_time, Newest);
        if (next >= Newest && shown < Newest) Holds++;
        Tick = Math.Max(shown, next);
    }

    public void Reset()
    {
        Tick = null;
        _time = 0;
        Newest = 0;
        Holds = 0;
    }
}
