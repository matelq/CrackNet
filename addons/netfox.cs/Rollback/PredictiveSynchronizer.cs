using Godot;
using Netfox.Internal;

namespace Netfox;

/// <summary>
/// RollbackSynchronizer without networking: keeps rollback state history and simulates nodes locally,
/// for short-lived or deterministic objects. Port of rollback/predictive-synchronizer.gd.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/netfox.cs/icons/predictive-synchronizer.svg")]
public partial class PredictiveSynchronizer : BaseSynchronizer
{
    private static readonly Dictionary<Node, PredictiveSynchronizer> ManagedRoots = new(ReferenceEqualityComparer.Instance);

    /// <summary>Node the property paths are relative to; defaults to the parent.</summary>
    [Export] public Node? Root { get; set; }

    /// <summary>State property paths in "Node:property" form, relative to Root.</summary>
    [ExportGroup("State")]
    [Export] public string[] StateProperties { get; set; } = [];

    public int SpawnTick { get; set; } = -1;

    private readonly PropertyPool _stateProperties = new();
    private readonly List<Node> _simNodes = new();
    private readonly List<Node> _livenessNodes = new();
    private Action? _spawnResimHandler;

    internal IReadOnlyList<Node> SimulatedNodes => _simNodes;

    protected override Node ResolveRoot() => Root ??= GetParent();

    protected override bool IsForeignRoot(Node node)
        => ManagedRoots.TryGetValue(node, out var owner) && owner != this;

    public override void ProcessSettings()
    {
        var root = ResolveRoot();
        var history = Context.NetworkHistoryServer;
        var simulation = Context.RollbackSimulationServer;
        var liveness = Context.RollbackLivenessServer;

        foreach (var subject in _stateProperties.Subjects.ToList())
            history.Deregister(subject);

        foreach (var node in _simNodes)
            simulation.DeregisterNode(node);
        _simNodes.Clear();

        var managedNodes = new List<Node> { root };
        managedNodes.AddRange(CollectManagedNodes(root));
        foreach (var node in managedNodes)
        {
            if (node is IRollbackTick rollbackAware)
            {
                _simNodes.Add(node);
                simulation.Register(node, rollbackAware.RollbackTick);
            }

            if (NetworkRollback.IsRollbackLivenessAware(node) && !liveness.IsRegistered(node))
            {
                liveness.Register(node,
                    NetworkRollback.GetRollbackSpawnMethod(node),
                    NetworkRollback.GetRollbackDespawnMethod(node),
                    NetworkRollback.GetRollbackDestroyMethod(node),
                    SpawnTick);
                _livenessNodes.Add(node);
            }
        }

        _stateProperties.SetFromPaths(root, StateProperties);
        foreach (var subject in _stateProperties.Subjects)
        {
            foreach (var property in _stateProperties.GetPropertiesOf(subject))
                history.RegisterRollbackState(subject, property);
            history.PushRollbackState(subject, SpawnTick);
        }
    }

    public void Spawn(int? tick = null)
    {
        var at = tick ?? Context.NetworkRollback.Tick;
        SpawnTick = at;

        var liveness = Context.RollbackLivenessServer;
        foreach (var node in _livenessNodes)
        {
            liveness.ClearDespawn(node);
            liveness.Spawn(node, at);
        }

        foreach (var subject in _stateProperties.Subjects)
            Context.NetworkHistoryServer.PushRollbackState(subject, at);
    }

    public void Despawn(int? tick = null)
    {
        var at = tick ?? Context.NetworkRollback.Tick;
        foreach (var node in _livenessNodes)
            Context.RollbackLivenessServer.Despawn(node, at);
    }

    public bool IsAlive(int? tick = null)
    {
        if (_livenessNodes.Count == 0) return true;
        return Context.RollbackLivenessServer.IsAlive(_livenessNodes[0], tick ?? Context.NetworkRollback.Tick);
    }

    /// <summary>Add a state property at runtime. Node may be a string, NodePath or Node relative to Root.</summary>
    public void AddState(object node, string property)
    {
        var path = PropertyEntry.MakePath(Root ?? GetParent(), node, property);
        if (path.Length == 0 || StateProperties.Contains(path)) return;

        StateProperties = [.. StateProperties, path];
        MarkPropertiesDirty();
    }

    public override void _Ready()
    {
        if (Engine.IsEditorHint()) return;

        Callable.From(ProcessSettings).CallDeferred();
        ReprocessOnConnect();
    }

    public override void _EnterTree()
    {
        base._EnterTree();
        if (Engine.IsEditorHint()) return;

        Root ??= GetParent();
        ManagedRoots[Root] = this;

        if (SpawnTick < 0) SpawnTick = Context.NetworkRollback.Tick + 1;

        var rollback = Context.NetworkRollback;
        _spawnResimHandler = () =>
        {
            rollback.BeforeLoop -= _spawnResimHandler;
            _spawnResimHandler = null;
            rollback.NotifyResimulationStart(SpawnTick);
        };
        rollback.BeforeLoop += _spawnResimHandler;
    }

    public override void _ExitTree()
    {
        if (Engine.IsEditorHint()) return;

        if (Root is not null) ManagedRoots.Remove(Root);

        if (_spawnResimHandler is not null && Context.NetworkRollback is { } rollback)
            rollback.BeforeLoop -= _spawnResimHandler;
        StopReprocessOnConnect();

        foreach (var node in _simNodes)
            Context.RollbackSimulationServer?.DeregisterNode(node);
        foreach (var node in _livenessNodes)
            Context.RollbackLivenessServer?.Deregister(node);
        foreach (var subject in _stateProperties.Subjects.ToList())
            Context.NetworkHistoryServer?.Deregister(subject);
    }

    public override string[] _GetConfigurationWarnings()
    {
        Root ??= GetParent();
        if (Root is null) return ["No valid root node found!"];

        return EditorUtils.GatherProperties<IRollbackStateProperties>(Root, n => n.GetRollbackStateProperties(), AddState);
    }
}
