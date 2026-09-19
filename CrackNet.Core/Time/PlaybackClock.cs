namespace CrackNet.Core.Time;

/// <summary>
/// The display tick for everything one remote peer sends. One clock per peer, not per object, so a stack of crates or
/// a character and what it holds are always shown at the same moment.
/// <para>
/// The clock trails the newest tick heard from the peer by an adaptive depth and slews its rate to hold it, rather than
/// jumping when packet spacing changes. The depth is the minimum - the send interval and a margin - plus the jitter this
/// link has shown recently: every arrival records how late it came against local time, and the spread of that over the
/// last few seconds is how much a packet can be late compared to its neighbours. A buffer absorbs send spacing and
/// jitter, never the base latency, so a clean link gets the minimum and a jittery one grows by its jitter and no more
/// (https://gafferongames.com/post/state_synchronization/, "jitter buffer"; Valorant's minimal buffering). The display never runs past the newest tick, but the clock's own time
/// keeps going while nothing arrives: a resting peer sends only heartbeats, and motion after a rest must show at the
/// normal depth at once, not a heartbeat late. After an outage that means a skip forward to where the data is, as
/// Source does, rather than seconds of added delay draining at a few percent. It never runs backwards.
/// </para>
/// </summary>
public sealed class PlaybackClock
{
    private const double Slew = 0.05;
    private const double DeadZone = 0.25;
    // Twenty seconds of arrivals, by time rather than count (a resting peer sends one a second), and percentiles
    // rather than min and max: a long window keeps the depth steady, and one outlier - the first packet, the one after
    // an outage, a hitch on the sending machine - must not inflate it for the whole window
    private const double LowPercentile = 0.05, HighPercentile = 0.95;

    // Below this many arrivals in the window the percentiles are just the extremes, and a first late packet with a few
    // heartbeats after it read as a second of jitter
    private const int MinSamplesForJitter = 10;
    private readonly Queue<(double At, double Lateness)> _lateness = new();
    private double _depth;
    private readonly double _maxDepth;
    private double _now;
    private const double CatchUpGain = 0.02;
    private const double MaxCatchUp = 0.5;
    private double _sinceNewest;
    private readonly double _delay;
    private readonly double _resyncDepth;
    private readonly double _maxLead;
    private readonly double _latenessWindow;
    private double _time;

    /// <param name="delayTicks">The minimum depth behind the newest tick: the send interval and a margin.</param>
    /// <param name="resyncTicks">
    /// How far behind the target the clock may fall before it jumps forward instead of slewing: after a long stall,
    /// catching up at 5% faster would take minutes.
    /// </param>
    /// <param name="maxLeadTicks">
    /// How far the clock's time may run past the newest tick while nothing arrives: at least the gap between heartbeats
    /// of a resting peer, so motion after a rest shows at the normal depth at once.
    /// </param>
    /// <param name="maxDepthTicks">The most jitter the buffer absorbs; past it, late packets are late.</param>
    /// <param name="latenessWindowTicks">How far back arrivals count towards the jitter: twenty seconds at 30 Hz.</param>
    public PlaybackClock(double delayTicks, double resyncTicks = 30, double maxLeadTicks = 40, double maxDepthTicks = 20,
        double latenessWindowTicks = 600)
    {
        _latenessWindow = latenessWindowTicks;
        _maxDepth = Math.Max(delayTicks, maxDepthTicks);
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

    /// <summary>
    /// Whether the peer has been heard from within the lead the clock's time may run past its newest tick: past that
    /// the time stands still, and a clock standing still must not hold back the screen's common display time.
    /// </summary>
    public bool IsLive => Tick is not null && _sinceNewest <= _maxLead;

    /// <summary>How far behind the newest tick the clock aims to run now: the minimum plus this link's recent jitter.</summary>
    public double Depth => _lateness.Count < MinSamplesForJitter ? _delay : _depth;

    private void UpdateDepth()
    {
        while (_lateness.Count > 0 && _now - _lateness.Peek().At > _latenessWindow) _lateness.Dequeue();
        if (_lateness.Count < MinSamplesForJitter)
        {
            _depth = _delay;
            return;
        }

        var sorted = _lateness.Select(entry => entry.Lateness).Order().ToArray();
        double At(double percentile) => sorted[(int)Math.Round(percentile * (sorted.Length - 1))];
        _depth = Math.Clamp(_delay + At(HighPercentile) - At(LowPercentile), _delay, _maxDepth);
    }

    /// <summary>The newest tick heard from the peer.</summary>
    public int Newest { get; private set; }

    /// <summary>Advances where the clock started holding at the newest tick; a measure of underruns.</summary>
    public int Holds { get; private set; }

    /// <summary>Tells the clock a sample for <paramref name="tick"/> arrived from the peer.</summary>
    public void Observe(int tick)
    {
        _lateness.Enqueue((_now, _now - tick));
        UpdateDepth();

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
        if (Newest - Depth - _time > _resyncDepth) _time = Newest - Depth;
        Tick = Math.Max(Tick.Value, Math.Min(_time, Newest));
    }

    /// <summary>Moves the display tick on by <paramref name="elapsedTicks"/> of local time.</summary>
    public void Advance(double elapsedTicks)
    {
        if (!double.IsFinite(elapsedTicks) || elapsedTicks < 0) throw new ArgumentOutOfRangeException(nameof(elapsedTicks));
        _now += elapsedTicks;
        if (Tick is not { } shown) return;

        // Time keeps running while nothing arrives: a peer whose objects rest sends only heartbeats, and a clock that
        // stopped at the last one would be a heartbeat behind when motion resumed, catching up at 5% for seconds.
        // Capped, so a long outage does not leave it running into a future nothing will fill.
        // Where the newest tick would be by now: a resting peer sends a heartbeat a second, and measuring against the
        // last one made the clock believe it was ahead for most of that second and never catch up
        _sinceNewest += elapsedTicks;
        var behind = Newest + Math.Min(_sinceNewest, _maxLead) - Depth - _time;

        // Ahead: ease back gently. Behind: speed up in proportion, up to half again, so a clock that started a second
        // late catches up in a couple of seconds instead of twenty - played back faster, never skipped
        var rate = Math.Abs(behind) <= DeadZone ? 1 : 1 + Math.Clamp(behind * CatchUpGain, -Slew, MaxCatchUp);
        _time = Math.Min(_time + elapsedTicks * rate, Newest + _maxLead);

        var next = Math.Min(_time, Newest);
        if (next >= Newest && shown < Newest) Holds++;
        Tick = Math.Max(shown, next);
    }

    public void Reset()
    {
        Tick = null;
        _time = 0;
        _now = 0;
        _lateness.Clear();
        _depth = _delay;
        _sinceNewest = 0;
        Newest = 0;
        Holds = 0;
    }
}
