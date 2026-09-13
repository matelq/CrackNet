using Godot;
using Netfox.Core.Logging;
using Netfox.Internal;

namespace Netfox;

/// <summary>
/// Tracks whether subjects were alive at a given tick, as a [spawn, despawn] interval.
/// Port of servers/rollback-liveness-server.gd.
/// </summary>
public partial class RollbackLivenessServer : Node
{
    public static RollbackLivenessServer Instance { get; private set; } = null!;

    /// <summary>The stack this server belongs to; resolved when it enters the tree.</summary>
    public NetfoxContext Context { get; private set; } = NetfoxContext.Default;

    private static readonly NetfoxLogger Logger = NetfoxLogger.ForNetfox("RollbackLivenessServer");

    private readonly Dictionary<Node, Action> _respawnCallback = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Node, Action> _despawnCallback = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Node, Action> _destroyCallback = new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<Node, int> _spawnTick = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Node, int> _despawnTick = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Node, bool> _appliedLiveness = new(ReferenceEqualityComparer.Instance);

    public override void _EnterTree()
    {
        NetfoxRuntime.EnsureInitialized();
        Context = NetfoxContext.For(this);
        Context.RollbackLivenessServer ??= this;
        if (Context.IsDefault) Instance ??= this;
    }

    public override void _ExitTree()
    {
        if (ReferenceEquals(Context.RollbackLivenessServer, this)) Context.RollbackLivenessServer = null!;
        if (Instance == this) Instance = null!;
    }

    /// <summary>
    /// Register <paramref name="subject"/> for liveness tracking. The callbacks run when rollback needs to
    /// respawn, despawn, or finally destroy the subject. Destroy defaults to QueueFree; spawn tick defaults to the current rollback tick.
    /// </summary>
    public void Register(Node subject, Action respawnCallback, Action despawnCallback, Action? destroyCallback = null, int? spawnTick = null)
    {
        if (IsRegistered(subject))
        {
            Logger.Warning("Re-registering subject: {0}", subject);
            return;
        }

        _respawnCallback[subject] = respawnCallback;
        _despawnCallback[subject] = despawnCallback;
        _destroyCallback[subject] = destroyCallback ?? subject.QueueFree;

        _appliedLiveness[subject] = true;
        Spawn(subject, spawnTick);
    }

    public bool IsRegistered(Node subject) => _respawnCallback.ContainsKey(subject);

    /// <summary>Stops tracking; the current liveness of the subject is left as-is.</summary>
    public void Deregister(Node subject)
    {
        _respawnCallback.Remove(subject);
        _despawnCallback.Remove(subject);
        _destroyCallback.Remove(subject);
        _spawnTick.Remove(subject);
        _despawnTick.Remove(subject);
        _appliedLiveness.Remove(subject);
    }

    /// <summary>
    /// Unknown subjects are always alive. A despawned subject is still alive on its despawn tick and dead after it,
    /// so the deactivating game logic can run in rollback.
    /// </summary>
    public bool IsAlive(Node subject, int tick)
    {
        if (!IsRegistered(subject)) return true;

        var spawnAt = _spawnTick.TryGetValue(subject, out var s) ? s : tick + 1;
        var despawnAt = _despawnTick.TryGetValue(subject, out var d) ? d : tick + 1;
        return tick >= spawnAt && tick <= despawnAt;
    }

    public void Spawn(Node subject, int? tick = null) => _spawnTick[subject] = tick ?? CurrentTick();

    public void Despawn(Node subject, int? tick = null)
    {
        if (!IsRegistered(subject))
        {
            Logger.Warning("Trying to despawn unknown subject: {0}; register it first using RollbackLivenessServer.Register()", subject);
            return;
        }
        _despawnTick[subject] = tick ?? CurrentTick();
    }

    public void ClearDespawn(Node subject) => _despawnTick.Remove(subject);

    /// <summary>Applies the liveness of every subject as it was on <paramref name="tick"/>.</summary>
    public void RestoreLiveness(int tick)
    {
        foreach (var subject in _respawnCallback.Keys.ToList())
        {
            var liveness = IsAlive(subject, tick);
            if (_appliedLiveness[subject] == liveness) continue;

            Logger.Trace("Restoring {0} to {1} liveness ( @{2};@{3} )", subject, liveness,
                _spawnTick.GetValueOrDefault(subject), _despawnTick.TryGetValue(subject, out var d) ? d : "-");
            if (liveness) _respawnCallback[subject]();
            else _despawnCallback[subject]();
            _appliedLiveness[subject] = liveness;
        }
    }

    /// <summary>Destroys subjects despawned before <paramref name="thresholdTick"/>, which defaults to the rollback history start.</summary>
    public void DestroyOldSubjects(int? thresholdTick = null)
    {
        var threshold = thresholdTick ?? Context.NetworkRollback?.HistoryStart ?? 0;

        var old = new List<Node>();
        foreach (var subject in _respawnCallback.Keys)
            if (_despawnTick.TryGetValue(subject, out var despawn) && despawn < threshold)
                old.Add(subject);

        foreach (var subject in old)
        {
            Logger.Trace("Freeing {0} as too old ( despawned at @{1} )", subject, _despawnTick[subject]);
            _destroyCallback[subject]();
            Deregister(subject);
        }
    }

    /// <summary>Respawns every registered subject at the current tick, dropping the previous session's spawn ticks.</summary>
    internal void ResetSession()
    {
        var tick = CurrentTick();
        _despawnTick.Clear();
        foreach (var subject in _respawnCallback.Keys.ToList())
        {
            _spawnTick[subject] = tick;
            _appliedLiveness[subject] = true;
        }
    }

    private int CurrentTick() => Context.NetworkRollback?.Tick ?? 0;
}
