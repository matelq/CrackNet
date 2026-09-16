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
    private const double CatchUpGain = 0.02;
    private const double MaxCatchUp = 0.5;
    private double _sinceNewest;
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

    /// <summary>
    /// The clock's own time, which keeps running while nothing arrives. How far this is behind the local tick is the
    /// peer's playback age; <see cref="Tick"/> can sit still at the newest sample while a peer rests.
    /// </summary>
    public double? Time => Tick is null ? null : _time;

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
        _sinceNewest = 0;
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
        // Where the newest tick would be by now: a resting peer sends a heartbeat a second, and measuring against the
        // last one made the clock believe it was ahead for most of that second and never catch up
        _sinceNewest += elapsedTicks;
        var behind = Newest + Math.Min(_sinceNewest, _maxLead) - _delay - _time;

        // Ahead: ease back gently. Behind: speed up in proportion, up to half again, so a clock that started a second
        // late catches up in a couple of seconds instead of twenty - played back faster, never skipped
        var rate = Math.Abs(behind) <= 1 ? 1 : 1 + Math.Clamp(behind * CatchUpGain, -Slew, MaxCatchUp);
        _time = Math.Min(_time + elapsedTicks * rate, Newest + _maxLead);

        var next = Math.Min(_time, Newest);
        if (next >= Newest && shown < Newest) Holds++;
        Tick = Math.Max(shown, next);
    }

    public void Reset()
    {
        Tick = null;
        _time = 0;
        _sinceNewest = 0;
        Newest = 0;
        Holds = 0;
    }
}
