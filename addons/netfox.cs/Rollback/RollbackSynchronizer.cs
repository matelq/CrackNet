using Godot;
using Netfox.Core.Logging;
using Netfox.Internal;

namespace Netfox;

/// <summary>
/// Configures rollback for a node tree: which properties are state, which are input, and which nodes get simulated.
/// Registers everything with the netfox servers. Port of rollback/rollback-synchronizer.gd.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/netfox.cs/icons/rollback-synchronizer.svg")]
public partial class RollbackSynchronizer : BaseSynchronizer
{
    private static readonly Dictionary<Node, RollbackSynchronizer> ManagedRoots = new(ReferenceEqualityComparer.Instance);

    /// <summary>Node the property paths are relative to; defaults to the parent.</summary>
    [Export] public Node? Root { get; set; }

    /// <summary>Simulate managed nodes even without up to date input.</summary>
    [Export]
    public bool EnablePrediction
    {
        get => _enablePrediction;
        set
        {
            if (value == _enablePrediction) return;
            _enablePrediction = value;
            SetPredictionEnabled(value);
        }
    }

    /// <summary>State property paths in "Node:property" form, relative to Root.</summary>
    [ExportGroup("State")]
    [Export] public string[] StateProperties { get; set; } = [];

    /// <summary>Input property paths in "Node:property" form, relative to Root.</summary>
    [ExportGroup("Inputs")]
    [Export] public string[] InputProperties { get; set; } = [];

    /// <summary>Controls which peers receive state. Added as a child automatically, under its own name so that it
    /// reads as itself in a saved scene rather than as a generated one.</summary>
    public PeerVisibilityFilter VisibilityFilter { get; set; } = new() { Name = "PeerVisibilityFilter" };

    /// <summary>Tick the managed nodes came to life. Defaults to the tick after entering the tree.</summary>
    public int SpawnTick { get; set; } = -1;

    private bool _enablePrediction;
    private readonly PropertyPool _stateProperties = new();
    private readonly PropertyPool _inputProperties = new();
    private readonly List<Node> _simNodes = new();
    private readonly List<Node> _livenessNodes = new();
    private NetfoxLogger _logger = NetfoxLogger.ForNetfox("RollbackSynchronizer");
    private Action? _spawnResimHandler;

    internal IReadOnlyList<Node> SimulatedNodes => _simNodes;

    /// <summary>Re-reads the configuration and registers nodes for simulation, liveness, identity and visibility.</summary>
    protected override Node ResolveRoot() => Root ??= GetParent();

    protected override bool IsForeignRoot(Node node)
        => ManagedRoots.TryGetValue(node, out var owner) && owner != this;

    public override void ProcessSettings()
    {
        var root = ResolveRoot();
        var simulation = Context.RollbackSimulationServer;
        var liveness = Context.RollbackLivenessServer;

        foreach (var node in _simNodes.Concat(_stateProperties.Subjects).Concat(_inputProperties.Subjects).ToList())
            simulation.DeregisterNode(node);
        _simNodes.Clear();

        ProcessAuthority();

        var managedNodes = new List<Node> { root };
        managedNodes.AddRange(CollectManagedNodes(root));
        _logger.Debug("Filtering managed nodes: {0}", string.Join(", ", managedNodes));

        foreach (var node in managedNodes)
        {
            if (node is IRollbackTick rollbackAware)
            {
                simulation.Register(node, rollbackAware.RollbackTick);
                simulation.SetPredictionEnabledFor(node, EnablePrediction);
                _simNodes.Add(node);
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

        // Both simulated and state nodes depend on all inputs
        foreach (var node in _simNodes.Concat(_stateProperties.Subjects))
            foreach (var inputNode in _inputProperties.Subjects)
                simulation.RegisterRollbackInputFor(node, inputNode);

        foreach (var node in _stateProperties.Subjects.Concat(_inputProperties.Subjects))
            Context.NetworkIdentityServer.RegisterNode(node);

        foreach (var node in _stateProperties.Subjects)
            Context.NetworkSynchronizationServer.RegisterVisibilityFilter(node, VisibilityFilter);
    }

    /// <summary>Re-registers properties, picking up authority changes. Called on connect.</summary>
    public void ProcessAuthority()
    {
        var root = Root ??= GetParent();
        var history = Context.NetworkHistoryServer;
        var synchronization = Context.NetworkSynchronizationServer;

        foreach (var node in _stateProperties.Subjects)
            foreach (var property in _stateProperties.GetPropertiesOf(node))
            {
                history.DeregisterRollbackState(node, property);
                synchronization.DeregisterRollbackState(node, property);
            }

        foreach (var node in _inputProperties.Subjects)
            foreach (var property in _inputProperties.GetPropertiesOf(node))
            {
                history.DeregisterRollbackInput(node, property);
                synchronization.DeregisterRollbackInput(node, property);
            }

        _stateProperties.SetFromPaths(root, StateProperties);
        _inputProperties.SetFromPaths(root, InputProperties);

        foreach (var node in _stateProperties.Subjects)
        {
            foreach (var property in _stateProperties.GetPropertiesOf(node))
            {
                history.RegisterRollbackState(node, property);
                synchronization.RegisterRollbackState(node, property);
            }
            history.PushRollbackState(node, SpawnTick);
        }

        foreach (var node in _inputProperties.Subjects)
            foreach (var property in _inputProperties.GetPropertiesOf(node))
            {
                history.RegisterRollbackInput(node, property);
                synchronization.RegisterRollbackInput(node, property);
            }
    }

    /// <summary>Add a state property at runtime. Node may be a string, NodePath or Node relative to Root.</summary>
    public void AddState(object node, string property)
    {
        var path = PropertyEntry.MakePath(Root ?? GetParent(), node, property);
        if (path.Length == 0 || StateProperties.Contains(path)) return;

        StateProperties = [.. StateProperties, path];
        MarkPropertiesDirty();
    }

    /// <summary>Add an input property at runtime. Node may be a string, NodePath or Node relative to Root.</summary>
    public void AddInput(object node, string property)
    {
        var path = PropertyEntry.MakePath(Root ?? GetParent(), node, property);
        if (path.Length == 0 || InputProperties.Contains(path)) return;

        InputProperties = [.. InputProperties, path];
        MarkPropertiesDirty();
    }

    /// <summary>Replace the serialization schema: property path to serializer.</summary>
    public void SetSchema(IReadOnlyDictionary<string, NetworkSchemaSerializer> schema) => SetSchemaInternal(schema);

    public void MergeSchema(IReadOnlyDictionary<string, NetworkSchemaSerializer> schema) => MergeSchemaInternal(schema);

    public void ClearSchema() => ClearSchemaInternal();

    /// <summary>Whether any input is known for the current rollback tick.</summary>
    public bool HasInput() => GetInputAge() >= 0;

    /// <summary>Age of the latest known input in ticks, or -1 if none.</summary>
    public int GetInputAge()
        => Context.NetworkHistoryServer.GetInputAgeFor(_inputProperties.Subjects, Context.NetworkRollback.Tick);

    /// <summary>True when the simulated node runs on guessed input, or, outside simulation, when current input is missing.</summary>
    public bool IsPredicting()
    {
        var simulation = Context.RollbackSimulationServer;
        if (simulation.GetSimulatedObject() is not null)
            return simulation.IsPredictingCurrent();
        return GetInputAge() != 0;
    }

    /// <summary>Do not record the state of <paramref name="node"/> during this rollback tick.</summary>
    public void IgnorePrediction(Node node) => Context.NetworkHistoryServer.Ignore(node);

    /// <summary>Latest tick with input for this synchronizer, or -1.</summary>
    public int GetLastKnownInput()
        => Context.NetworkHistoryServer.GetLatestInputFor(_inputProperties.Subjects, Context.NetworkTime.Tick);

    /// <summary>Age of the latest known state in ticks, or -1.</summary>
    public int GetLastKnownState()
        => Context.NetworkHistoryServer.GetStateAgeFor(_stateProperties.Subjects, Context.NetworkTime.Tick);

    /// <summary>Mark the managed nodes as spawned at <paramref name="tick"/> and seed their state.</summary>
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

    protected override void OnSessionReset()
    {
        // The spawn tick belongs to the session that ended; as far as the new one is concerned, these nodes spawn now
        SpawnTick = Context.NetworkRollback.Tick + 1;
        base.OnSessionReset();
    }

    public override void _Ready()
    {
        if (Engine.IsEditorHint()) return;

        Root ??= GetParent();
        _logger = NetfoxLogger.ForNetfox("RollbackSynchronizer:" + Root.Name);

        if (SpawnTick < 0) SpawnTick = Context.NetworkRollback.Tick + 1;
        Callable.From(ProcessSettings).CallDeferred();
        ReprocessOnConnect();
    }

    public override void _EnterTree()
    {
        base._EnterTree();
        if (Engine.IsEditorHint()) return;

        Root ??= GetParent();
        ManagedRoots[Root] = this;

        // Resimulate from spawn tick, only on the next loop
        var rollback = Context.NetworkRollback;
        _spawnResimHandler = () =>
        {
            rollback.BeforeLoop -= _spawnResimHandler;
            _spawnResimHandler = null;
            rollback.NotifyResimulationStart(SpawnTick);
        };
        rollback.BeforeLoop += _spawnResimHandler;

        VisibilityFilter ??= new PeerVisibilityFilter { Name = "PeerVisibilityFilter" };
        if (VisibilityFilter.GetParent() is null)
            AddChild(VisibilityFilter);
    }

    public override void _ExitTree()
    {
        if (Engine.IsEditorHint()) return;

        if (Root is not null) ManagedRoots.Remove(Root);

        // Godot auto-disconnects signals of freed nodes; C# events need explicit cleanup
        if (_spawnResimHandler is not null && Context.NetworkRollback is { } rollback)
            rollback.BeforeLoop -= _spawnResimHandler;
        StopReprocessOnConnect();

        // Consider the synchronizer and its nodes freed, deregister everything
        foreach (var node in _simNodes.Concat(_stateProperties.Subjects).Concat(_inputProperties.Subjects).ToList())
        {
            Context.RollbackSimulationServer?.DeregisterNode(node);
            Context.NetworkSynchronizationServer?.Deregister(node);
            Context.NetworkIdentityServer?.DeregisterNode(node);
            Context.NetworkHistoryServer?.Deregister(node);
        }

        foreach (var node in _livenessNodes)
            Context.RollbackLivenessServer?.Deregister(node);
    }

    public override string[] _GetConfigurationWarnings()
    {
        Root ??= GetParent();
        if (Root is null) return ["No valid root node found!"];

        var warnings = new List<string>();
        warnings.AddRange(EditorUtils.GatherProperties<IRollbackStateProperties>(Root, n => n.GetRollbackStateProperties(), AddState));
        warnings.AddRange(EditorUtils.GatherProperties<IRollbackInputProperties>(Root, n => n.GetRollbackInputProperties(), AddInput));
        return warnings.ToArray();
    }

    private void SetPredictionEnabled(bool enabled)
    {
        var simulation = Context.RollbackSimulationServer;
        if (simulation is null) return;
        foreach (var node in _simNodes)
            simulation.SetPredictionEnabledFor(node, enabled);
    }
}
