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
[Icon("res://addons/netfox-net/icons/rollback-synchronizer.svg")]
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

    /// <summary>
    /// Show this node from the last tick its input is actually known for, instead of from the predicted head.
    /// <para>
    /// Where this peer is the node's state authority and someone else drives its input, the newest ticks are simulated
    /// from input that is still a round trip away and corrected when it lands - a remote player on the host's screen
    /// moves, snaps back, moves, and the host is the only screen that shows it. With this on, the node is displayed
    /// from the state after the last tick with real input, behind a delay that covers the worst input age of the last
    /// second so the node moves one tick per tick, and the TickInterpolator smooths it as usual. What is drawn is then
    /// always something that happened. The simulation is untouched - only what is put on the node between loops
    /// changes - and a node whose input is this peer's own, or whose state this peer does not own, is left alone.
    /// </para>
    /// <para>
    /// Not for observers. A peer that merely receives a node's state cannot tell "unchanged" from "not arrived" -
    /// an unchanged node sends nothing, by design of the diffs - so neither the age of the latest state nor the
    /// spacing of arrivals measures freshness there; both were tried and both held a standing player seconds in the
    /// past once it moved again. Hiding arrival jitter on an observer needs a freshness signal on the wire first.
    /// netfox-net#38.
    /// </para>
    /// </summary>
    [Export] public bool DisplayKnownOnly { get; set; }

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

        WarnAboutUnlistedAttributes(root);
    }

    /// <summary>
    /// Attribute properties under Root that this synchronizer does not replicate, as "Node:property" paths. Empty
    /// when the scene lists everything the code declares.
    /// </summary>
    public IReadOnlyList<string> UnlistedAttributeProperties => _unlisted;

    private readonly List<string> _unlisted = new();

    /// <summary>
    /// A [RollbackState] or [RollbackInput] attribute is gathered into StateProperties/InputProperties by the editor
    /// plugin when the scene is saved - and at no other time. Headless, or with a scene saved before the property
    /// existed, the attribute is decoration: the property is never registered and nothing says so. That cost a day
    /// once (netfox-net#58): a new input never left the machine that pressed it, and a check passed anyway. So the
    /// same gather runs here at runtime, and every declared path the lists do not carry gets a warning naming it.
    /// The lists stay the source of truth; this only refuses to be quiet about the difference.
    /// </summary>
    private void WarnAboutUnlistedAttributes(Node root)
    {
        _unlisted.Clear();
        EditorUtils.GatherProperties<IRollbackStateProperties>(root, n => n.GetRollbackStateProperties(),
            (node, property) => NoteIfUnlisted(root, node, property, StateProperties, "StateProperties"));
        EditorUtils.GatherProperties<IRollbackInputProperties>(root, n => n.GetRollbackInputProperties(),
            (node, property) => NoteIfUnlisted(root, node, property, InputProperties, "InputProperties"));
    }

    private void NoteIfUnlisted(Node root, Node node, string property, string[] listed, string listName)
    {
        var path = PropertyEntry.MakePath(root, node, property);
        if (path.Length == 0 || listed.Contains(path)) return;
        _unlisted.Add(path);
        _logger.Warning("{0} declares a rollback attribute on \"{1}\" but {2} does not list it - it is not replicated. Add \"{1}\" to {2} in the scene, or save the scene in the editor.",
            Name, path, listName);
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

    /// <summary>
    /// The display half of <see cref="DisplayKnownOnly"/>: puts the state recorded after the last tick with real
    /// input back on the managed nodes, over the state for the display tick that the rollback loop just restored.
    /// </summary>
    private void DisplayFromKnown()
    {
        if (!DisplayKnownOnly || _inputProperties.IsEmpty) return;
        if (_inputProperties.Subjects.All(subject => subject.IsMultiplayerAuthority())) return; // our own input: nothing is guessed
        if (!_stateProperties.Subjects.All(subject => subject.IsMultiplayerAuthority())) return; // not ours to know better

        var history = Context.NetworkHistoryServer;
        var now = Context.NetworkTime.Tick;
        var known = history.GetLatestInputFor(_inputProperties.Subjects, now) + 1;
        if (known <= 0) return;

        // A jitter buffer rather than the newest real tick: data arrives in bunches, and a display that advances as
        // data arrives stands still and then leaps. The delay is the worst input age of the last three seconds, so it
        // changes only when the link does. Growing it moves the node back a tick once; shrinking it skips a tick
        // once. Slewing through those instead of stepping is netfox-net#39, the same smoothing corrections need.
        _recentAges[now % _recentAges.Length] = now - known;
        _recentAgeTicks[now % _recentAges.Length] = now;
        var delay = 0;
        var window = Context.NetworkTime.Tickrate * 3;
        for (var i = 0; i < _recentAges.Length; i++)
            if (now - _recentAgeTicks[i] < window) delay = Math.Max(delay, _recentAges[i]);

        // Growing the delay moves the node back a tick, once, and is rare. Shrinking it would skip a tick forward
        // every time the link quietens - a hitch on every walk. So the delay only shrinks when nobody could see it:
        // when the node's state is the same at the tick shown now and at the tick that would be shown instead.
        if (delay < DisplayDelayTicks && !SameState(now - DisplayDelayTicks, now - delay)) delay = DisplayDelayTicks;

        DisplayDelayTicks = delay;
        history.RestoreRollbackState(Math.Min(Context.NetworkRollback.DisplayTick, now - delay), _stateProperties.Subjects);
    }

    private bool SameState(int tickA, int tickB)
    {
        var history = Context.NetworkHistoryServer;
        var a = history.GetRollbackStateSnapshot(tickA);
        var b = history.GetRollbackStateSnapshot(tickB);
        if (a is null || b is null) return false;
        foreach (var subject in _stateProperties.Subjects)
            foreach (var property in _stateProperties.GetPropertiesOf(subject))
            {
                if (!a.TryGetProperty(subject, property, out var va) || !b.TryGetProperty(subject, property, out var vb)) return false;
                if (!Snapshot.ValueComparer.Equals(va, vb)) return false;
            }
        return true;
    }

    /// <summary>How many ticks behind the present this node is currently shown, when <see cref="DisplayKnownOnly"/> is on. Zero for a node driven by this peer's own input.</summary>
    public int DisplayDelayTicks { get; private set; }
    private readonly int[] _recentAges = new int[256];
    private readonly int[] _recentAgeTicks = new int[256];

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
        rollback.AfterDisplayRestore += DisplayFromKnown;

        VisibilityFilter ??= new PeerVisibilityFilter { Name = "PeerVisibilityFilter" };
        if (VisibilityFilter.GetParent() is null)
            AddChild(VisibilityFilter);
    }

    public override void _ExitTree()
    {
        if (Engine.IsEditorHint()) return;

        if (Root is not null) ManagedRoots.Remove(Root);

        // Godot auto-disconnects signals of freed nodes; C# events need explicit cleanup
        if (Context.NetworkRollback is { } rollback)
        {
            if (_spawnResimHandler is not null) rollback.BeforeLoop -= _spawnResimHandler;
            rollback.AfterDisplayRestore -= DisplayFromKnown;
        }

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
