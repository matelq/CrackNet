using Godot;
using Netfox.Core.Logging;
using Netfox.Core.Time;
using Netfox.Internal;

namespace Netfox;

/// <summary>Drives network ticks and keeps them synced to the host. Port of network-time.gd.</summary>
public partial class NetworkTime : Node
{
    public static NetworkTime Instance { get; private set; } = null!;

    private static readonly NetfoxLogger Logger = NetfoxLogger.ForNetfox("NetworkTime");

    private enum State { Inactive, Syncing, Active }

    private readonly TickClock _clock = new()
    {
        Tickrate = Settings.GetInt("netfox/time/tickrate", 30),
        SyncToPhysics = Settings.GetBool("netfox/time/sync_to_physics", false),
        MaxTicksPerFrame = Settings.GetInt("netfox/time/max_ticks_per_frame", 8),
        StallThreshold = Settings.GetDouble("netfox/time/stall_threshold", 1.0),
        ClockStretchMax = Settings.GetDouble("netfox/time/max_time_stretch", 1.25),
    };

    private readonly double _recalibrateThreshold = Settings.GetDouble("netfox/time/recalibrate_threshold", 8.0);
    private readonly bool _suppressOfflinePeerWarning = Settings.GetBool("netfox/time/suppress_offline_peer_warning", false);

    private State _state = State.Inactive;
    private bool _initialSyncDone;
    private double _processDelta;
    private readonly HashSet<int> _syncedPeers = new();
    private NetworkTickrateHandshake _tickrateHandshake = null!;
    private readonly Func<string> _tickTag;

    /// <summary>Emitted before a tick loop is run.</summary>
    public event Action? BeforeTickLoop;
    /// <summary>(delta, tick)</summary>
    public event Action<double, int>? BeforeTick;
    /// <summary>(delta, tick)</summary>
    public event Action<double, int>? OnTick;
    /// <summary>(delta, tick)</summary>
    public event Action<double, int>? AfterTick;
    /// <summary>Emitted after the tick loop is run.</summary>
    public event Action? AfterTickLoop;
    /// <summary>Emitted after time is synchronized; instantly on the server.</summary>
    public event Action? AfterSync;
    /// <summary>Emitted on the server when a client finishes its time sync. (peer id)</summary>
    public event Action<int>? AfterClientSync;
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

    public bool SyncToPhysics => _clock.SyncToPhysics;
    public int MaxTicksPerFrame => _clock.MaxTicksPerFrame;

    /// <summary>Current network time in seconds, continuously synced with the server.</summary>
    public double Time => (double)Tick / Tickrate;

    /// <summary>Current network time in ticks, continuously synced with the server.</summary>
    public int Tick => _clock.Tick;

    [Obsolete("Use NetworkTimeSynchronizer.PanicThreshold instead")]
    public double RecalibrateThreshold => _recalibrateThreshold;

    /// <summary>Seconds without frames before the game is considered stalled and catch-up ticks are skipped.</summary>
    public double StallThreshold => _clock.StallThreshold;

    [Obsolete("Returns the same as Tick")] public int RemoteTick => Tick;
    [Obsolete("Returns the same as Time")] public double RemoteTime => Time;
    [Obsolete("Returns the same as Tick")] public int LocalTick => Tick;
    [Obsolete("Returns the same as Time")] public double LocalTime => Time;

    /// <summary>Estimated roundtrip time to the server. Always 0 on the server.</summary>
    public double RemoteRtt => NetworkTimeSynchronizer.Instance.Rtt;

    /// <summary>Duration of a single tick, in seconds.</summary>
    public double Ticktime => 1.0 / Tickrate;

    /// <summary>0.0 right after a tick, 1.0 right before the next.</summary>
    public double TickFactor => SyncToPhysics ? Engine.GetPhysicsInterpolationFraction() : _clock.TickFactor;

    /// <summary>Multiplier from physics-process speeds to tick speeds; multiply velocities by it around MoveAndSlide.</summary>
    public double PhysicsFactor => Engine.IsInPhysicsFrame()
        ? Engine.PhysicsTicksPerSecond / (double)Tickrate
        : Ticktime / _processDelta;

    public double ClockStretchMax => _clock.ClockStretchMax;
    public bool SuppressOfflinePeerWarning => _suppressOfflinePeerWarning;

    /// <summary>Current clock speed multiplier; above 1.0 speeds up to catch the host, below slows down.</summary>
    public double ClockStretchFactor => _clock.StretchFactor;

    /// <summary>Reference clock minus simulation clock.</summary>
    public double ClockOffset => _clock.ClockOffset(NetworkTimeSynchronizer.Instance.GetTime());

    /// <summary>Same as NetworkTimeSynchronizer.RemoteOffset.</summary>
    public double RemoteClockOffset => NetworkTimeSynchronizer.Instance.RemoteOffset;

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
                "under netfox/Time/Suppress Offline Peer Warning.");

        if (SyncToPhysics) _clock.Tickrate = Engine.PhysicsTicksPerSecond;

        _clock.Tick = 0;
        _initialSyncDone = false;
        _syncedPeers.Add(1); // Host is always synced, their time is ground truth

        var synchronizer = NetworkTimeSynchronizer.Instance;
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
                Rpc(MethodName.SubmitSyncSuccess);
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

        Multiplayer.PeerDisconnected += HandlePeerDisconnect;

        _clock.Reset(NetworkTimeSynchronizer.Instance.GetTime());
        AfterSync?.Invoke();

        _tickrateHandshake.Run();
    }

    /// <summary>Stop NetworkTime and the background sync. No ticks until the next Start.</summary>
    public void Stop()
    {
        NetworkTimeSynchronizer.Instance.Stop();
        _tickrateHandshake.Stop();

        _state = State.Inactive;
        _syncedPeers.Clear();
        _clock.Tick = 0;
        _initialSyncDone = false;

        if (GodotObject.IsInstanceValid(Multiplayer))
            Multiplayer.PeerDisconnected -= HandlePeerDisconnect;
    }

    public bool IsInitialSyncDone() => _initialSyncDone;

    /// <summary>Whether the given client finished its time sync. Only meaningful on the server.</summary>
    public bool IsClientSynced(int peerId) => _syncedPeers.Contains(peerId);

    public double TicksToSeconds(int ticks) => ticks * Ticktime;
    public int SecondsToTicks(double seconds) => (int)(seconds * Tickrate);
    public double SecondsBetween(int tickFrom, int tickTo) => TicksToSeconds(tickTo - tickFrom);
    public int TicksBetween(double secondsFrom, double secondsTo) => SecondsToTicks(secondsTo - secondsFrom);

    public override void _EnterTree()
    {
        NetfoxRuntime.EnsureInitialized();
        Instance ??= this;
    }

    public override void _Ready()
    {
        NetfoxLogger.RegisterTag(_tickTag, -100);

        _tickrateHandshake = new NetworkTickrateHandshake();
        AddChild(_tickrateHandshake);
        _tickrateHandshake.OnTickrateMismatch += (peer, tickrate) => OnTickrateMismatch?.Invoke(peer, tickrate);
    }

    public override void _ExitTree()
    {
        NetfoxLogger.FreeTag(_tickTag);
        if (Instance == this) Instance = null!;
    }

    public override void _Process(double delta)
    {
        _processDelta = delta;
        if (_state != State.Active) return;

        if (!SyncToPhysics) Loop();
        InterpolationServer.Instance?.Interpolate(TickFactor);
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
        var ticksInLoop = _clock.Advance(NetworkTimeSynchronizer.Instance.GetTime());

        if (ticksInLoop > 0)
        {
            RunBeforeTickLoop();

            for (var i = 0; i < ticksInLoop; i++)
            {
                var tick = Tick;
                var delta = Ticktime;
                BeforeTick?.Invoke(delta, tick);
                OnTick?.Invoke(delta, tick);
                AfterTick?.Invoke(delta, tick);

                NetworkRollback.Instance?.AfterTick(tick);
                NetworkHistoryServer.Instance?.RecordSyncState(tick + 1);
                NetworkSynchronizationServer.Instance?.SynchronizeSyncState(tick + 1);

                _clock.CompleteTick();
            }

            RunAfterTickLoop();
        }

        NetworkIdentityServer.Instance?.FlushQueue();
    }

    /// <summary>Test hook: runs the pre-loop stage and emits BeforeTickLoop.</summary>
    internal void RunBeforeTickLoop()
    {
        InterpolationServer.Instance?.ClearTeleports();
        InterpolationServer.Instance?.ApplyTargetState();
        BeforeTickLoop?.Invoke();
    }

    /// <summary>Test hook: runs the rollback loop and post-loop stage, emits AfterTickLoop.</summary>
    internal void RunAfterTickLoop()
    {
        NetworkRollback.Instance?.Rollback();
        AfterTickLoop?.Invoke();
        NetworkHistoryServer.Instance?.RestoreSynchronizerState(Tick);
        InterpolationServer.Instance?.RecordNextState();
    }

    /// <summary>Test hook: emits the per-tick events for the current tick and advances it.</summary>
    internal void RunTick(Action? body = null)
    {
        BeforeTick?.Invoke(Ticktime, Tick);
        OnTick?.Invoke(Ticktime, Tick);
        body?.Invoke();
        AfterTick?.Invoke(Ticktime, Tick);
        _clock.CompleteTick();
    }

    /// <summary>Test hook: overrides the current tick.</summary>
    internal void SetTick(int tick) => _clock.Tick = tick;

    private void HandlePeerDisconnect(long peer) => _syncedPeers.Remove((int)peer);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SubmitSyncSuccess()
    {
        var peerId = Multiplayer.GetRemoteSenderId();
        Logger.Trace("Received time sync success from #{0}, synced peers: {1}", peerId, string.Join(", ", _syncedPeers));

        if (_syncedPeers.Add(peerId))
        {
            AfterClientSync?.Invoke(peerId);
            Logger.Debug("Peer #{0} is now on time!", peerId);
        }
    }
}
