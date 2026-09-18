using CrackNet.Core.Logging;
using CrackNet.Core.Time;
using CrackNet.Internal;
using Godot;

namespace CrackNet;

/// <summary>
/// The shared tick clock: runs ticks at a fixed rate and keeps them in step with the host. Started and stopped by
/// <see cref="NetworkEvents"/> with the session; samples are stamped with <see cref="Tick"/>.
/// </summary>
public partial class NetworkTime : Node
{
    public static NetworkTime Instance { get; private set; } = null!;

    /// <summary>The stack this server belongs to; resolved when it enters the tree.</summary>
    public CrackNetContext Context { get; private set; } = CrackNetContext.Default;

    private static readonly CrackNetLogger Logger = CrackNetLogger.ForCrackNet("NetworkTime");

    private enum State { Inactive, Syncing, Active }

    private readonly TickClock _clock = new()
    {
        Tickrate = CrackNetSettings.Instance.Tickrate,
        SyncToPhysics = CrackNetSettings.Instance.SyncToPhysics,
        MaxTicksPerFrame = CrackNetSettings.Instance.MaxTicksPerFrame,
        StallThreshold = CrackNetSettings.Instance.StallThreshold,
        ClockStretchMax = CrackNetSettings.Instance.MaxTimeStretch,
    };

    private readonly bool _suppressOfflinePeerWarning = CrackNetSettings.Instance.SuppressOfflinePeerWarning;

    private State _state = State.Inactive;
    private bool _initialSyncDone;
    private NetworkTickrateHandshake _tickrateHandshake = null!;
    private readonly Func<string> _tickTag;

    /// <summary>Every tick: (delta, tick).</summary>
    public event Action<double, int>? OnTick;
    /// <summary>After every tick's <see cref="OnTick"/>, when state is sent: (delta, tick).</summary>
    public event Action<double, int>? AfterTick;
    /// <summary>Emitted after time is synchronized; instantly on the server.</summary>
    public event Action? AfterSync;
    /// <summary>(peer, tickrate). Emitted when the tickrate mismatch action is Signal.</summary>
    public event Action<int, int>? OnTickrateMismatch;

    public NetworkTime()
    {
        _tickTag = () => $"@{Tick}";
    }

    /// <summary>Ticks per second. Equals the physics tickrate when SyncToPhysics is on.</summary>
    public int Tickrate
    {
        get => SyncToPhysics ? Engine.PhysicsTicksPerSecond : _clock.Tickrate;
        internal set => _clock.Tickrate = value;
    }

    private bool SyncToPhysics => _clock.SyncToPhysics;

    /// <summary>Current network time in seconds, continuously synced with the server.</summary>
    public double Time => (double)Tick / Tickrate;

    /// <summary>Current network time in ticks, continuously synced with the server.</summary>
    public int Tick => _clock.Tick;

    /// <summary>Estimated roundtrip time to the server. Always 0 on the server.</summary>
    public double RemoteRtt => Context.NetworkTimeSynchronizer.Rtt;

    /// <summary>Duration of a single tick, in seconds.</summary>
    public double Ticktime => 1.0 / Tickrate;

    /// <summary>0.0 right after a tick, 1.0 right before the next.</summary>
    public double TickFactor => SyncToPhysics ? Engine.GetPhysicsInterpolationFraction() : _clock.TickFactor;


    /// <summary>
    /// Start NetworkTime: synchronize with the host, then emit ticks. On clients, ticks start after the initial sync.
    /// Returns Ok, AlreadyInUse if already running, or Unavailable without a multiplayer peer.
    /// </summary>
    public Error Start()
    {
        if (_state != State.Inactive)
        {
            Logger.Warning("Multiple calls to NetworkTime.Start()! Are you manually calling *and* have NetworkEvents enabled?");
            return Error.AlreadyInUse;
        }

        if (!Multiplayer.HasMultiplayerPeer())
        {
            Logger.Error("Starting time loop without a multiplayer peer!");
            return Error.Unavailable;
        }

        if (Multiplayer.MultiplayerPeer is OfflineMultiplayerPeer && !_suppressOfflinePeerWarning)
            Logger.Warning("Starting time loop with an offline peer! " +
                "If this is intended, suppress this warning in the project settings, " +
                "under cracknet/Time/Suppress Offline Peer Warning.");

        if (SyncToPhysics) _clock.Tickrate = Engine.PhysicsTicksPerSecond;

        _clock.Tick = 0;
        _initialSyncDone = false;

        var synchronizer = Context.NetworkTimeSynchronizer;
        synchronizer.Start();
        _state = State.Syncing;

        if (!Multiplayer.IsServer())
        {
            Action? onSynced = null;
            onSynced = () =>
            {
                synchronizer.OnInitialSync -= onSynced;
                if (_state != State.Syncing) return;
                _clock.Tick = SecondsToTicks(synchronizer.GetTime());
                Activate();
            };
            synchronizer.OnInitialSync += onSynced;
        }
        else
        {
            Activate();
        }

        return Error.Ok;
    }

    private void Activate()
    {
        _initialSyncDone = true;
        _state = State.Active;

        _clock.Reset(Context.NetworkTimeSynchronizer.GetTime());
        AfterSync?.Invoke();

        _tickrateHandshake.Run();
    }

    /// <summary>Stop NetworkTime and the background sync. No ticks until the next Start.</summary>
    public void Stop()
    {
        Context.NetworkTimeSynchronizer.Stop();
        _tickrateHandshake.Stop();

        _state = State.Inactive;
        _clock.Tick = 0;
        _initialSyncDone = false;
    }

    public bool IsInitialSyncDone() => _initialSyncDone;

    private int SecondsToTicks(double seconds) => (int)(seconds * Tickrate);

    public override void _EnterTree()
    {
        CrackNetRuntime.EnsureInitialized();
        Context = CrackNetContext.For(this);
        Context.NetworkTime ??= this;
        if (Context.IsDefault) Instance ??= this;
    }

    public override void _Ready()
    {
        CrackNetLogger.RegisterTag(_tickTag, -100);

        _tickrateHandshake = new NetworkTickrateHandshake();
        AddChild(_tickrateHandshake);
        _tickrateHandshake.OnTickrateMismatch += (peer, tickrate) => OnTickrateMismatch?.Invoke(peer, tickrate);
    }

    public override void _ExitTree()
    {
        CrackNetLogger.FreeTag(_tickTag);
        if (ReferenceEquals(Context.NetworkTime, this)) Context.NetworkTime = null!;
        if (Instance == this) Instance = null!;
    }

    public override void _Process(double delta)
    {
        if (_state != State.Active) return;

        if (!SyncToPhysics) Loop();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_state == State.Active && SyncToPhysics) Loop();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationUnpaused) _clock.WasPaused = true;
    }

    private void Loop()
    {
        var ticksInLoop = _clock.Advance(Context.NetworkTimeSynchronizer.GetTime());

        if (ticksInLoop > 0)
        {
            for (var i = 0; i < ticksInLoop; i++)
            {
                var tick = Tick;
                var delta = Ticktime;
                OnTick?.Invoke(delta, tick);
                AfterTick?.Invoke(delta, tick);
                _clock.CompleteTick();
            }
        }

        Context.NetworkIdentityServer?.FlushQueue();
    }
}
