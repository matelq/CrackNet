using Godot;
using Netfox.Core.Logging;
using Netfox.Core.Serialization;

namespace Netfox;

/// <summary>
/// A replicated set of ticks at which a discrete event happened. Peers toggle it inside rollback ticks; the authority
/// broadcasts the ground truth, and predictions get confirmed or cancelled. Port of rewindable-action.gd.
/// </summary>
[GlobalClass]
[Icon("res://addons/netfox.cs/icons/rewindable-action.svg")]
public partial class RewindableAction : Node
{
    public enum Status
    {
        Inactive,
        Confirming,
        Active,
        Cancelling,
    }

    private readonly SortedSet<int> _activeTicks = new();
    private int _lastSetTick = -1;

    // Ticks changed during this loop, and what they changed to
    private readonly Dictionary<int, bool> _stateChanges = new();
    // Ticks queued for change by the authority, and what they change to
    private readonly Dictionary<int, bool> _queuedChanges = new();

    private bool _hasConfirmed;
    private bool _hasCancelled;

    private readonly Dictionary<int, object?> _context = new();
    private readonly HashSet<GodotObject> _mutatedObjects = new(ReferenceEqualityComparer.Instance);

    private NetfoxLogger _logger = NetfoxLogger.ForNetfox("RewindableAction");

    public static string StatusString(Status status) => status.ToString().ToUpperInvariant();

    /// <summary>Toggle the action for <paramref name="tick"/>, defaulting to the current rollback tick.</summary>
    public void SetActive(bool active, int? tick = null)
    {
        var at = tick ?? NetworkRollback.Instance.Tick;
        _lastSetTick = at;

        if (IsActive(at) == active) return;

        if (active) _activeTicks.Add(at);
        else _activeTicks.Remove(at);

        _stateChanges[at] = active;
    }

    public bool IsActive(int? tick = null) => _activeTicks.Contains(tick ?? NetworkRollback.Instance.Tick);

    public Status GetStatus(int? tick = null)
    {
        var at = tick ?? NetworkRollback.Instance.Tick;
        var currentlyActive = IsActive(at);

        if (_queuedChanges.TryGetValue(at, out var queued))
            return queued ? Status.Confirming : Status.Cancelling;
        if (_stateChanges.TryGetValue(at, out var changed))
            return changed ? Status.Confirming : Status.Cancelling;
        return currentlyActive ? Status.Active : Status.Inactive;
    }

    /// <summary>True if any tick got confirmed during the last loop.</summary>
    public bool HasConfirmed() => _hasConfirmed;

    /// <summary>True if any tick got cancelled during the last loop.</summary>
    public bool HasCancelled() => _hasCancelled;

    public string GetStatusString(int? tick = null) => StatusString(GetStatus(tick));

    public bool HasContext(int? tick = null) => _context.ContainsKey(tick ?? NetworkRollback.Instance.Tick);

    /// <summary>Arbitrary data remembered per tick, e.g. the projectile spawned by this action.</summary>
    public object? GetContext(int? tick = null) => _context.GetValueOrDefault(tick ?? NetworkRollback.Instance.Tick);

    public T? GetContext<T>(int? tick = null) => GetContext(tick) is T value ? value : default;

    public void SetContext(object? value, int? tick = null) => _context[tick ?? NetworkRollback.Instance.Tick] = value;

    public void EraseContext(int? tick = null) => _context.Remove(tick ?? NetworkRollback.Instance.Tick);

    /// <summary>Resimulate <paramref name="target"/> whenever this action changes.</summary>
    public void Mutate(GodotObject target) => _mutatedObjects.Add(target);

    public void DontMutate(GodotObject target) => _mutatedObjects.Remove(target);

    public override void _EnterTree()
    {
        _logger = NetfoxLogger.ForNetfox("RewindableAction:" + Name);
        var rollback = NetworkRollback.Instance;
        rollback.BeforeLoop += BeforeRollbackLoop;
        rollback.AfterLoop += AfterLoop;
    }

    public override void _ExitTree()
    {
        var rollback = NetworkRollback.Instance;
        if (rollback is null) return;
        rollback.BeforeLoop -= BeforeRollbackLoop;
        rollback.AfterLoop -= AfterLoop;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationPathRenamed)
            _logger = NetfoxLogger.ForNetfox("RewindableAction:" + Name);
    }

    private void BeforeRollbackLoop()
    {
        var rollback = NetworkRollback.Instance;
        _lastSetTick = -1;

        if (_queuedChanges.Count > 0)
        {
            // Resimulate from the earliest change
            var earliestChange = _queuedChanges.Keys.Min();
            rollback.NotifyResimulationStart(earliestChange);
            _logger.Trace("Submitted earliest tick {0} from {1} queued changes", earliestChange, _queuedChanges.Count);

            foreach (var mutated in _mutatedObjects)
                rollback.Mutate(mutated, earliestChange);

            foreach (var (tick, active) in _queuedChanges.ToList())
                SetActive(active, tick);
        }

        if (_activeTicks.Count > 0)
        {
            var earliestActive = _activeTicks.Min;
            foreach (var mutated in _mutatedObjects)
                rollback.Mutate(mutated, earliestActive);
        }
    }

    private void AfterLoop()
    {
        var historyStart = NetworkRollback.Instance.HistoryStart;

        _activeTicks.RemoveWhere(tick => tick < historyStart);
        foreach (var tick in _context.Keys.Where(tick => tick < historyStart).ToList())
            _context.Remove(tick);

        _hasConfirmed = _stateChanges.Values.Any(v => v) || _queuedChanges.Values.Any(v => v);
        _hasCancelled = _stateChanges.Values.Any(v => !v) || _queuedChanges.Values.Any(v => !v);

        _stateChanges.Clear();
        _queuedChanges.Clear();

        if (IsMultiplayerAuthority() && _lastSetTick >= 0)
        {
            var serializeFrom = historyStart;
            var serializeTo = _lastSetTick;
            if (_activeTicks.Count > 0)
                serializeTo = Math.Max(_activeTicks.Max, serializeTo);

            var bytes = TicksetSerializer.Serialize(serializeFrom, serializeTo, _activeTicks);
            Rpc(MethodName.SubmitState, bytes);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void SubmitState(byte[] bytes) => ReceiveState(bytes);

    /// <summary>Ingests the authoritative tickset; queues differences for the next loop. Exposed for tests.</summary>
    internal void ReceiveState(byte[] bytes)
    {
        var (historyStart, lastKnownTick, activeTicks) = TicksetSerializer.Deserialize(bytes);
        var rollbackHistoryStart = NetworkRollback.Instance.HistoryStart;

        var earliestTick = Math.Max(historyStart, rollbackHistoryStart);
        // Do not compare past the last event, so events the host does not know about yet are not cancelled
        var latestTick = Math.Max(lastKnownTick, rollbackHistoryStart);

        // Tolerate a few ticks in the future; the server may be slightly ahead under tiny latencies
        var currentTick = NetworkTime.Instance.Tick;
        if (earliestTick > currentTick + 4 || latestTick > currentTick + 4)
            _logger.Debug("Received tickset for range @{0}>{1}, which has ticks in the future!", earliestTick, latestTick);

        for (var tick = earliestTick; tick <= latestTick; tick++)
        {
            var isTickActive = activeTicks.Contains(tick);
            if (isTickActive != IsActive(tick))
                _queuedChanges[tick] = isTickActive;
        }
    }
}
