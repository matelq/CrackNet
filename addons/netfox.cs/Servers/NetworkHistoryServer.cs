using Godot;
using Netfox.Core.Collections;
using Netfox.Core.Logging;
using Netfox.Internal;

namespace Netfox;

/// <summary>
/// Tracks the history of rollback state, rollback input, and synchronized state properties.
/// Port of servers/network-history-server.gd.
/// </summary>
public partial class NetworkHistoryServer : Node
{
    public static NetworkHistoryServer Instance { get; private set; } = null!;

    private static readonly NetfoxLogger Logger = NetfoxLogger.ForNetfox("NetworkHistoryServer");

    private readonly PropertyPool _rbInputProperties = new();
    private readonly PropertyPool _rbStateProperties = new();
    private readonly PropertyPool _syncStateProperties = new();

    private readonly int _rbHistorySize = NetfoxSettings.Instance.RollbackHistoryLimit;
    private readonly int _syncHistorySize = NetfoxSettings.Instance.StateSyncHistoryLimit;

    private readonly HashSet<Node> _ignoredSubjects = new(ReferenceEqualityComparer.Instance);

    // Source of truth for history
    private readonly PerObjectHistory _rbInputHistory;
    private readonly PerObjectHistory _rbStateHistory;
    private readonly PerObjectHistory _syncHistory;

    // Cached per-tick snapshots for syncing
    private readonly HistoryBuffer<Snapshot> _rbInputSnapshots;
    private readonly HistoryBuffer<Snapshot> _rbStateSnapshots;
    private readonly HistoryBuffer<Snapshot> _syncStateSnapshots;

    public NetworkHistoryServer()
    {
        _rbInputHistory = new PerObjectHistory(_rbHistorySize);
        _rbStateHistory = new PerObjectHistory(_rbHistorySize);
        _syncHistory = new PerObjectHistory(_syncHistorySize);
        _rbInputSnapshots = new HistoryBuffer<Snapshot>(_rbHistorySize);
        _rbStateSnapshots = new HistoryBuffer<Snapshot>(_rbHistorySize);
        _syncStateSnapshots = new HistoryBuffer<Snapshot>(_syncHistorySize);
    }

    public override void _EnterTree()
    {
        NetfoxRuntime.EnsureInitialized();
        Instance ??= this;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null!;
    }

    public void RegisterRollbackState(Node node, NodePath property) => _rbStateProperties.Add(node, property);
    public void DeregisterRollbackState(Node node, NodePath property) => _rbStateProperties.Erase(node, property);
    public void RegisterRollbackInput(Node node, NodePath property) => _rbInputProperties.Add(node, property);
    public void DeregisterRollbackInput(Node node, NodePath property) => _rbInputProperties.Erase(node, property);
    public void RegisterSyncState(Node node, NodePath property) => _syncStateProperties.Add(node, property);
    public void DeregisterSyncState(Node node, NodePath property) => _syncStateProperties.Erase(node, property);

    /// <summary>Stop tracking every property of <paramref name="node"/> and drop its history.</summary>
    public void Deregister(Node node)
    {
        _rbStateProperties.EraseSubject(node);
        _rbInputProperties.EraseSubject(node);
        _syncStateProperties.EraseSubject(node);

        _rbStateHistory.EraseSubject(node);
        _rbInputHistory.EraseSubject(node);
        _syncHistory.EraseSubject(node);

        foreach (var history in new[] { _rbStateSnapshots, _rbInputSnapshots, _syncStateSnapshots })
            foreach (var snapshot in history.Values())
                snapshot.EraseSubject(node);
    }

    /// <summary>Do not record <paramref name="subject"/> until FlushIgnores, which runs after every rollback tick.</summary>
    public void Ignore(Node subject) => _ignoredSubjects.Add(subject);

    public void FlushIgnores() => _ignoredSubjects.Clear();

    /// <summary>Latest tick at or before <paramref name="tick"/> where any of the subjects has rollback state, or -1.</summary>
    public int GetLatestStateTickFor(IEnumerable<Node> subjects, int tick) => GetLatestFor(subjects, tick, _rbStateHistory);

    /// <summary>Age in ticks of the latest rollback state of any of the subjects, or -1.</summary>
    public int GetStateAgeFor(IEnumerable<Node> subjects, int tick)
    {
        var latest = GetLatestStateTickFor(subjects, tick);
        return latest < 0 ? -1 : tick - latest;
    }

    /// <summary>Latest tick at or before <paramref name="tick"/> where any of the subjects has rollback input, or -1.</summary>
    public int GetLatestInputFor(IEnumerable<Node> subjects, int tick) => GetLatestFor(subjects, tick, _rbInputHistory);

    /// <summary>Age in ticks of the latest rollback input of any of the subjects, or -1.</summary>
    public int GetInputAgeFor(IEnumerable<Node> subjects, int tick)
    {
        var latest = GetLatestInputFor(subjects, tick);
        return latest < 0 ? -1 : tick - latest;
    }

    /// <summary>Record the registered rollback state properties of <paramref name="subject"/> at <paramref name="tick"/>, e.g. to seed spawn state.</summary>
    public void PushRollbackState(Node subject, int tick)
    {
        var subjectSnapshot = _rbStateHistory.EnsureSnapshot(tick, subject, carryForward: false);
        if (subjectSnapshot is null)
        {
            Logger.Warning("Dropping seeded state @{0} for subject {1} as out-of-bounds", tick, subject);
            return;
        }

        var snapshot = EnsureTickSnapshot(_rbStateSnapshots, tick);
        var isAuth = subject.IsMultiplayerAuthority();
        foreach (var property in _rbStateProperties.GetPropertiesOf(subject))
        {
            subjectSnapshot.RecordProperty(property);
            snapshot.RecordProperty(subject, property);
        }

        snapshot.SetAuth(subject, isAuth);
        subjectSnapshot.IsAuth = isAuth;
    }

    internal void RecordRollbackInput(int tick)
        => Record(tick, _rbInputHistory, _rbInputSnapshots, _rbInputProperties, onlyAuth: true, subject => subject.IsMultiplayerAuthority());

    internal void RecordRollbackState(int tick)
    {
        var inputSnapshot = GetRollbackInputSnapshot(tick - 1);
        var simulation = RollbackSimulationServer.Instance;

        Record(tick, _rbStateHistory, _rbStateSnapshots, _rbStateProperties, onlyAuth: false, subject =>
        {
            if (!subject.IsMultiplayerAuthority()) return false;
            if (simulation is not null && simulation.IsPredicting(inputSnapshot, subject)) return false;
            return true;
        });
    }

    internal void RecordSyncState(int tick)
        => Record(tick, _syncHistory, _syncStateSnapshots, _syncStateProperties, onlyAuth: true, subject => subject.IsMultiplayerAuthority());

    internal bool RestoreRollbackInput(int tick) => RestoreLatest(tick, _rbInputHistory);
    internal bool RestoreRollbackState(int tick) => RestoreLatest(tick, _rbStateHistory);
    internal bool RestoreSynchronizerState(int tick) => RestoreLatest(tick, _syncHistory);

    internal Snapshot? GetRollbackInputSnapshot(int tick) => _rbInputSnapshots.GetAt(tick);
    internal Snapshot? GetRollbackStateSnapshot(int tick) => _rbStateSnapshots.GetAt(tick);
    internal Snapshot? GetSynchronizerStateSnapshot(int tick) => _syncStateSnapshots.GetAt(tick);

    internal bool MergeRollbackInput(Snapshot snapshot)
    {
        MergeSnapshot(snapshot, _rbInputSnapshots, reverse: true);
        return MergeHistory(snapshot, _rbInputHistory, reverse: true);
    }

    internal bool MergeRollbackState(Snapshot snapshot)
    {
        MergeSnapshot(snapshot, _rbStateSnapshots);
        return MergeHistory(snapshot, _rbStateHistory);
    }

    internal bool MergeSynchronizerState(Snapshot snapshot)
    {
        MergeSnapshot(snapshot, _syncStateSnapshots, reverse: true);
        return MergeHistory(snapshot, _syncHistory);
    }

    private void Record(int tick, PerObjectHistory history, HistoryBuffer<Snapshot> snapshots, PropertyPool propertyPool, bool onlyAuth, Func<Node, bool> authFilter)
    {
        var snapshot = EnsureTickSnapshot(snapshots, tick);
        var liveness = RollbackLivenessServer.Instance;

        foreach (var subject in propertyPool.Subjects.ToList())
        {
            // Do not record history when the subject is not alive, to prevent state corruption
            if (ReferenceEquals(history, _rbStateHistory) && liveness is not null && !liveness.IsAlive(subject, tick - 1))
                continue;

            if (_ignoredSubjects.Contains(subject)) continue;

            var isAuth = authFilter(subject);
            if (onlyAuth && !isAuth) continue;
            if (!isAuth && history.IsAuth(tick, subject)) continue;

            var subjectSnapshot = history.EnsureSnapshot(tick, subject, carryForward: false);
            if (subjectSnapshot is null)
            {
                // Usually the tick is close to history start and time sync is off by just enough to push it over the edge
                Logger.Warning("Dropping recorded tick @{0} for subject {1} as out-of-bounds", tick, subject);
                continue;
            }

            foreach (var property in propertyPool.GetPropertiesOf(subject))
            {
                var value = subject.GetIndexed(property);
                subjectSnapshot.SetValue(property, value);
                snapshot.SetProperty(subject, property, value);
            }
            snapshot.SetAuth(subject, isAuth);
            subjectSnapshot.IsAuth = isAuth;
        }

        if (Logger.CheckLevel(LogLevel.Trace))
        {
            if (ReferenceEquals(history, _rbInputHistory)) Logger.Trace("Recorded input @{0}: {1}", tick, snapshot);
            else if (ReferenceEquals(history, _rbStateHistory)) Logger.Trace("Recorded state @{0}: {1}", tick, snapshot);
        }
    }

    private bool RestoreLatest(int tick, PerObjectHistory history)
    {
        var anyApplied = false;

        foreach (var subject in history.Subjects.ToList())
        {
            var snapshot = history.GetLatestSnapshot(tick, subject);
            if (snapshot is null) continue;
            if (!GodotObject.IsInstanceValid(subject)) continue;

            snapshot.Apply();
            anyApplied = true;

            if (Logger.CheckLevel(LogLevel.Trace))
            {
                if (ReferenceEquals(history, _rbInputHistory)) Logger.Trace("Restored input @{0}: {1}", tick, snapshot);
                else if (ReferenceEquals(history, _rbStateHistory)) Logger.Trace("Restored state @{0}: {1}", tick, snapshot);
            }
        }

        return anyApplied;
    }

    private static bool MergeSnapshot(Snapshot snapshot, HistoryBuffer<Snapshot> snapshots, bool reverse = false)
    {
        var tick = snapshot.Tick;

        if (!snapshots.TryGetAt(tick, out var original))
        {
            snapshots.SetAt(tick, snapshot);
            return true;
        }

        if (!reverse) return original.Merge(snapshot);

        // Merge the original snapshot on top of the incoming one, so peers cannot rewrite their past inputs
        var originalSubjects = original.AuthSubjects;
        var incomingSubjects = snapshot.AuthSubjects.ToList();

        snapshots.SetAt(tick, snapshot);
        snapshot.Merge(original);

        // Only true if we received data for a new subject
        return incomingSubjects.Any(subject => !originalSubjects.Contains(subject));
    }

    private bool MergeHistory(Snapshot snapshot, PerObjectHistory history, bool reverse = false)
    {
        var tick = snapshot.Tick;
        var hasUpdated = false;

        var historyStart = NetworkRollback.Instance?.HistoryStart ?? 0;
        if (tick < historyStart)
        {
            if (ReferenceEquals(history, _rbInputHistory)) Logger.Warning("Input being merged is too old! (@{0})", tick);
            else if (ReferenceEquals(history, _rbStateHistory)) Logger.Warning("State being merged is too old! (@{0})", tick);
            else Logger.Warning("Sync state being merged is too old! (@{0})", tick);
            return false;
        }

        foreach (var subject in snapshot.Subjects)
        {
            var objectSnapshot = history.EnsureSnapshot(tick, subject, carryForward: !reverse);
            if (objectSnapshot is null)
            {
                Logger.Warning("Snapshot being merged is out of bounds! (@{0})", tick);
                continue;
            }

            // Never overwrite auth data with non-auth data
            if (objectSnapshot.IsAuth && !snapshot.IsAuth(subject)) continue;

            foreach (var (property, newValue) in snapshot.GetSubjectData(subject))
            {
                // In reverse, only accept previously unknown values
                if (reverse && objectSnapshot.HasValue(property)) continue;

                var hadValue = objectSnapshot.TryGetValue(property, out var originalValue);
                objectSnapshot.SetValue(property, newValue);
                if (!hasUpdated && (!hadValue || !Snapshot.ValueComparer.Equals(originalValue, newValue)))
                    hasUpdated = true;
            }
            objectSnapshot.IsAuth = snapshot.IsAuth(subject);
        }

        return hasUpdated;
    }

    private static int GetLatestFor(IEnumerable<Node> subjects, int tick, PerObjectHistory history)
    {
        var latest = -1;
        foreach (var subject in subjects)
        {
            var subjectLatest = history.GetLatestTick(tick, subject);
            if (subjectLatest < 0) continue;
            latest = Math.Max(latest, subjectLatest);
        }
        return latest;
    }

    private static Snapshot EnsureTickSnapshot(HistoryBuffer<Snapshot> snapshots, int tick)
    {
        if (snapshots.TryGetAt(tick, out var existing)) return existing;
        var snapshot = new Snapshot(tick);
        snapshots.SetAt(tick, snapshot);
        return snapshot;
    }
}
