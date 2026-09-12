using Godot;
using Netfox.Core.Logging;
using Netfox.Internal;

namespace Netfox;

/// <summary>Runs the rollback loop: restore history, resimulate, record, broadcast. Port of rollback/network-rollback.gd.</summary>
public partial class NetworkRollback : Node
{
    public static NetworkRollback Instance { get; private set; } = null!;

    /// <summary>The stack this server belongs to; resolved when it enters the tree.</summary>
    public NetfoxContext Context { get; private set; } = NetfoxContext.Default;

    private static readonly NetfoxLogger Logger = NetfoxLogger.ForNetfox("NetworkRollback");

    private const string StageBefore = "B";
    private const string StagePrepare = "P";
    private const string StageSimulate = "S";
    private const string StageRecord = "R";
    private const string StageAfter = "A";

    /// <summary>Whether the rollback loop runs at all. From netfox/rollback/enabled.</summary>
    public bool Enabled { get; set; } = NetfoxSettings.Instance.RollbackEnabled;

    /// <summary>Whether to send only changed properties. From netfox/rollback/enable_diff_states.</summary>
    public bool EnableDiffStates { get; set; } = NetfoxSettings.Instance.RollbackEnableDiffStates;

    /// <summary>How many ticks back history is kept and rollback can go. From netfox/rollback/history_limit.</summary>
    public int HistoryLimit { get; } = NetfoxSettings.Instance.RollbackHistoryLimit;

    /// <summary>Show this many ticks in the past to hide corrections. From netfox/rollback/display_offset.</summary>
    public int DisplayOffset { get; } = NetfoxSettings.Instance.DisplayOffset;

    /// <summary>Record inputs this many ticks into the future. From netfox/rollback/input_delay.</summary>
    public int InputDelay { get; } = NetfoxSettings.Instance.InputDelay;

    private readonly int _inputRedundancy = NetfoxSettings.Instance.InputRedundancy;

    /// <summary>How many past inputs are resent with every input packet, at least 1.</summary>
    public int InputRedundancy => Math.Max(1, _inputRedundancy);

    /// <summary>First tick that can still be rolled back to.</summary>
    public int HistoryStart => Math.Max(0, Context.NetworkTime.Tick - HistoryLimit);

    /// <summary>The tick shown on screen after the rollback loop.</summary>
    public int DisplayTick => Enabled ? Math.Max(0, Context.NetworkTime.Tick - DisplayOffset) : Context.NetworkTime.Tick;

    /// <summary>First tick of the current rollback loop, -1 outside of it.</summary>
    public int RollbackFrom => _rollbackFrom;

    /// <summary>The tick currently being resimulated. Only meaningful during rollback.</summary>
    public int Tick => _tick;

    /// <summary>Emitted before the rollback loop; call NotifyResimulationStart here.</summary>
    public event Action? BeforeLoop;
    /// <summary>Emitted before state is restored for the tick.</summary>
    public event Action<int>? OnPrepareTick;
    /// <summary>Emitted after state is restored for the tick.</summary>
    public event Action<int>? AfterPrepareTick;
    /// <summary>Emitted before the tick is simulated.</summary>
    public event Action<int>? OnProcessTick;
    /// <summary>Emitted after the tick is simulated.</summary>
    public event Action<int>? AfterProcessTick;
    /// <summary>Emitted before the resulting state is recorded; carries tick + 1.</summary>
    public event Action<int>? OnRecordTick;
    /// <summary>Emitted after the rollback loop.</summary>
    public event Action? AfterLoop;

    private int _tick;
    private int _resimFrom;
    private int _rollbackFrom = -1;
    private int _rollbackTo = -1;
    private string _rollbackStage = "";

    private bool _isRollback;
    private readonly HashSet<Node> _simulatedNodes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<GodotObject, int> _mutatedNodes = new(ReferenceEqualityComparer.Instance);

    private int _earliestInput = -1;
    private int _earliestState = -1;

    private readonly Func<string> _rollbackTag;

    public NetworkRollback()
    {
        _rollbackTag = () => _isRollback ? $"{_rollbackStage}@{_tick}|{_rollbackFrom}>{_rollbackTo}" : "_";
    }

    /// <summary>Request resimulation from <paramref name="tick"/>; the earliest request wins. Call from BeforeLoop.</summary>
    public void NotifyResimulationStart(int tick) => _resimFrom = Math.Min(_resimFrom, tick);

    public void NotifySimulated(Node node) => _simulatedNodes.Add(node);

    public bool IsSimulated(Node node) => _simulatedNodes.Contains(node);

    /// <summary>True while the rollback loop is running.</summary>
    public bool IsRollback() => _isRollback;

    public static bool IsRollbackAware(GodotObject what) => what is IRollbackTick;

    public static bool IsRollbackLivenessAware(GodotObject what)
        => what is IRollbackSpawnAware or IRollbackDespawnAware or IRollbackDestroyAware;

    public static bool IsRollbackSpawnAware(GodotObject what) => what is IRollbackSpawnAware;
    public static bool IsRollbackDespawnAware(GodotObject what) => what is IRollbackDespawnAware;
    public static bool IsRollbackDestroyAware(GodotObject what) => what is IRollbackDestroyAware;

    public static void ProcessRollback(IRollbackTick target, double delta, int tick, bool isFresh) => target.RollbackTick(delta, tick, isFresh);

    /// <summary>Mark <paramref name="target"/> as changed from <paramref name="tick"/> on, so it gets resimulated even without input.</summary>
    public void Mutate(GodotObject target, int? tick = null)
    {
        var at = tick ?? _tick;
        _mutatedNodes[target] = _mutatedNodes.TryGetValue(target, out var existing) ? Math.Min(at, existing) : at;

        if (_isRollback && at < _tick)
            Logger.Warning("Trying to mutate object {0} in the past, for tick {1}!", target, at);
    }

    public bool IsMutated(GodotObject target, int? tick = null)
        => _mutatedNodes.TryGetValue(target, out var mutatedAt) && (tick ?? _tick) >= mutatedAt;

    public bool IsJustMutated(GodotObject target, int? tick = null)
        => _mutatedNodes.TryGetValue(target, out var mutatedAt) && mutatedAt == (tick ?? _tick);

    /// <summary>Latest tick with input available for <paramref name="node"/>, or -1.</summary>
    public int GetLatestInputTick(Node node)
    {
        var inputNodes = Context.RollbackSimulationServer.GetInputsOf(node);
        return Context.NetworkHistoryServer.GetLatestInputFor(inputNodes, Context.NetworkTime.Tick);
    }

    public bool HasInputForTick(Node node, int tick)
    {
        var latestInput = GetLatestInputTick(node);
        return latestInput != -1 && latestInput >= tick;
    }

    internal static Action GetRollbackSpawnMethod(GodotObject target)
        => target is IRollbackSpawnAware aware ? aware.RollbackSpawn : static () => { };

    internal static Action GetRollbackDespawnMethod(GodotObject target)
        => target is IRollbackDespawnAware aware ? aware.RollbackDespawn : static () => { };

    internal static Action GetRollbackDestroyMethod(GodotObject target)
    {
        if (target is IRollbackDestroyAware aware) return aware.RollbackDestroy;
        if (target is Node node) return node.QueueFree;
        return target.Free;
    }

    public override void _EnterTree()
    {
        NetfoxRuntime.EnsureInitialized();
        Context = NetfoxContext.For(this);
        Context.NetworkRollback ??= this;
        if (Context.IsDefault) Instance ??= this;
    }

    public override void _Ready()
    {
        NetfoxLogger.RegisterTag(_rollbackTag);

        if (Context.NetworkSynchronizationServer is { } sync)
        {
            sync.OnInput += HandleInput;
            sync.OnState += HandleState;
        }
    }

    public override void _ExitTree()
    {
        NetfoxLogger.FreeTag(_rollbackTag);
        if (ReferenceEquals(Context.NetworkRollback, this)) Context.NetworkRollback = null!;
        if (Instance == this) Instance = null!;
    }

    /// <summary>The rollback loop. Called by NetworkTime after every tick loop.</summary>
    internal void Rollback()
    {
        if (!Enabled) return;

        var networkTime = Context.NetworkTime;
        var history = Context.NetworkHistoryServer;
        var liveness = Context.RollbackLivenessServer;
        var simulation = Context.RollbackSimulationServer;
        var synchronization = Context.NetworkSynchronizationServer;

        // Ask all rewindables to submit their earliest inputs
        _resimFrom = networkTime.Tick;
        BeforeLoop?.Invoke();

        var rangeSource = "notif";
        if (_earliestInput >= 0 && _earliestInput <= _resimFrom)
        {
            rangeSource = "earliest input";
            _resimFrom = _earliestInput;
        }
        if (_earliestState >= 0 && _earliestState <= _resimFrom)
        {
            rangeSource = "latest state";
            _resimFrom = _earliestState;
        }
        _resimFrom = Math.Min(_resimFrom, networkTime.Tick - 1);
        Logger.Trace("Simulating range @{0}>@{1} using {2}", _resimFrom, networkTime.Tick, rangeSource);

        // Only set _isRollback after emitting BeforeLoop
        _isRollback = true;
        _rollbackStage = StageBefore;

        var from = _resimFrom;
        var to = networkTime.Tick;

        if (to - from > HistoryLimit)
        {
            Logger.Warning("Trying to run rollback for ticks {0} to {1}, past the history limit of {2}", from, to, HistoryLimit);
            from = networkTime.Tick - HistoryLimit;
        }

        _earliestInput = -1;
        _earliestState = -1;

        _rollbackFrom = from;
        _rollbackTo = to;
        for (var tick = from; tick < to; tick++)
        {
            _tick = tick;
            _simulatedNodes.Clear();

            // Prepare: restore input and state for the tick
            _rollbackStage = StagePrepare;
            OnPrepareTick?.Invoke(tick);
            history.RestoreRollbackInput(tick);
            history.RestoreRollbackState(tick);
            liveness.RestoreLiveness(tick);
            AfterPrepareTick?.Invoke(tick);

            // Simulate
            _rollbackStage = StageSimulate;
            OnProcessTick?.Invoke(tick);
            simulation.Simulate(networkTime.Ticktime, tick);
            AfterProcessTick?.Invoke(tick);

            // Record state for tick + 1
            _rollbackStage = StageRecord;
            OnRecordTick?.Invoke(tick + 1);
            history.RecordRollbackState(tick + 1);
            history.FlushIgnores();
        }

        // Send state once per loop, for the newest tick only. Upstream sends inside the loop (network-rollback.gd:429),
        // so resimulating a range re-broadcasts every tick in it, every frame, to every peer; the corrections are
        // already contained in the newest tick's state. See foxssake/netfox#630.
        if (to > from) synchronization.SynchronizeState(to);

        // Restore display state
        _rollbackStage = StageAfter;
        AfterLoop?.Invoke();
        history.RestoreRollbackState(DisplayTick);
        liveness.RestoreLiveness(DisplayTick);
        simulation.TrimTicksSimulated(HistoryStart);
        liveness.DestroyOldSubjects(HistoryStart);

        _mutatedNodes.Clear();
        _isRollback = false;
    }

    /// <summary>Records and sends input for the tick. Called by NetworkTime after every tick.</summary>
    internal void AfterTick(int tick)
    {
        Context.NetworkHistoryServer.RecordRollbackInput(tick + InputDelay);
        Context.NetworkSynchronizationServer.SynchronizeInput(tick + InputDelay);
    }

    private void HandleInput(Snapshot snapshot)
    {
        if (snapshot.IsEmpty) return;
        if (_earliestInput < 0 || snapshot.Tick < _earliestInput)
        {
            Logger.Trace("Ingested input @{0}, earliest @{1}->@{2}", snapshot.Tick, _earliestInput, snapshot.Tick);
            _earliestInput = snapshot.Tick;
        }
        else
        {
            Logger.Trace("Ingested input @{0}, earliest @{1}->@{2}", snapshot.Tick, _earliestInput, _earliestInput);
        }
    }

    private void HandleState(Snapshot snapshot)
    {
        if (snapshot.IsEmpty) return;
        if (_earliestState < 0 || snapshot.Tick < _earliestState)
        {
            Logger.Trace("Ingested state @{0}, latest @{1}->@{2}", snapshot.Tick, _earliestState, snapshot.Tick);
            _earliestState = snapshot.Tick;
        }
        else
        {
            Logger.Trace("Ingested state @{0}, latest @{1}->@{2}", snapshot.Tick, _earliestState, _earliestState);
        }
    }

    /// <summary>Test hooks.</summary>
    internal void SetTick(int tick) => _tick = tick;
    internal void SetIsRollback(bool value) => _isRollback = value;
}
