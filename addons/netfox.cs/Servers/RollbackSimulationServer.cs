using Godot;
using Netfox.Core.Collections;
using Netfox.Core.Logging;
using Netfox.Internal;

namespace Netfox;

/// <summary>
/// Runs gameplay simulation during rollback: tracks which nodes participate, decides which need simulating for a tick
/// (in scene tree order), and calls their callbacks. Port of servers/rollback-simulation-server.gd.
/// </summary>
public partial class RollbackSimulationServer : Node
{
    public static RollbackSimulationServer Instance { get; private set; } = null!;

    /// <summary>The stack this server belongs to; resolved when it enters the tree.</summary>
    public NetfoxContext Context { get; private set; } = NetfoxContext.Default;

    private static readonly NetfoxLogger Logger = NetfoxLogger.ForNetfox("RollbackSimulationServer");

    private NetworkHistoryServer? _historyServer;
    private RollbackLivenessServer? _livenessServer;

    private readonly Dictionary<Node, Action<double, int, bool>> _callbacks = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Node, List<int>> _simulatedTicks = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Node> _predictionEnabledNodes = new(ReferenceEqualityComparer.Instance);
    private readonly Graph<Node> _inputGraph = new(); // Links inputs to the nodes they control
    private readonly HashSet<Node> _predictedNodes = new(ReferenceEqualityComparer.Instance);
    private Node? _currentObject;
    private StringName _group = null!;

    public RollbackSimulationServer() { }

    public RollbackSimulationServer(NetworkHistoryServer? historyServer, RollbackLivenessServer? livenessServer)
    {
        _historyServer = historyServer;
        _livenessServer = livenessServer;
    }

    public override void _EnterTree()
    {
        NetfoxRuntime.EnsureInitialized();
        Context = NetfoxContext.For(this);
        Context.RollbackSimulationServer ??= this;
        if (Context.IsDefault) Instance ??= this;
        _group = new StringName("__nf_rollback_sim" + GetInstanceId());
    }

    public override void _Ready()
    {
        _historyServer ??= Context.NetworkHistoryServer;
        _livenessServer ??= Context.RollbackLivenessServer;
    }

    public override void _ExitTree()
    {
        if (ReferenceEquals(Context.RollbackSimulationServer, this)) Context.RollbackSimulationServer = null!;
        if (Instance == this) Instance = null!;
    }

    /// <summary>Register a rollback tick callback for <paramref name="subject"/>. One callback per node.</summary>
    public void Register(Node subject, Action<double, int, bool> callback)
    {
        if (!GodotObject.IsInstanceValid(subject))
        {
            Logger.Error("Trying to register callback that belongs to an invalid object!");
            return;
        }
        if (_callbacks.ContainsKey(subject))
            Logger.Error("Double Register() of node {0}!", subject);
        if (!subject.IsInsideTree())
            Logger.Error("Node {0} is not inside the tree!", subject);

        _callbacks[subject] = callback;
        subject.AddToGroup(_group);
    }

    /// <summary>Register a rollback-aware node.</summary>
    public void Register<T>(T node) where T : Node, IRollbackTick => Register(node, node.RollbackTick);

    /// <summary>Remove <paramref name="subject"/> from the rollback loop.</summary>
    public void Deregister(Node subject)
    {
        if (!_callbacks.Remove(subject)) return;
        _inputGraph.Erase(subject);
        _simulatedTicks.Remove(subject);
        if (GodotObject.IsInstanceValid(subject)) subject.RemoveFromGroup(_group);
    }

    /// <summary>Remove <paramref name="node"/> and all its input links and prediction settings.</summary>
    public void DeregisterNode(Node node)
    {
        Deregister(node);
        _inputGraph.Erase(node);
        _predictionEnabledNodes.Remove(node);
    }

    /// <summary>Register <paramref name="input"/> as providing input for <paramref name="node"/>.</summary>
    public void RegisterRollbackInputFor(Node node, Node input) => _inputGraph.Link(input, node);

    public void DeregisterRollbackInput(Node node, Node input) => _inputGraph.Unlink(input, node);

    /// <summary>Prediction means the node is simulated even without up to date input.</summary>
    public void SetPredictionEnabledFor(Node node, bool enabled)
    {
        if (enabled) _predictionEnabledNodes.Add(node);
        else _predictionEnabledNodes.Remove(node);
    }

    public bool IsPredictionEnabledFor(Node node) => _predictionEnabledNodes.Contains(node);

    /// <summary>True if the node currently being simulated is predicted.</summary>
    public bool IsPredictingCurrent()
    {
        if (_currentObject is null || !GodotObject.IsInstanceValid(_currentObject)) return false;
        return _predictedNodes.Contains(_currentObject);
    }

    public Node? GetSimulatedObject() => _currentObject;

    /// <summary>Simulate a single rollback tick.</summary>
    public void Simulate(double delta, int tick)
    {
        _currentObject = null;

        var history = _historyServer ?? Context.NetworkHistoryServer;
        var inputSnapshot = history.GetRollbackInputSnapshot(tick);
        var nodes = GetNodesToSimulate(inputSnapshot); // Sorted by tree order
        _predictedNodes.Clear();

        foreach (var node in _callbacks.Keys)
            if (IsPredicting(inputSnapshot, node))
                _predictedNodes.Add(node);

        foreach (var node in nodes)
        {
            _currentObject = node;
            var isFresh = IsTickFreshFor(node, tick);
            _callbacks[node](delta, tick, isFresh);
            _currentObject = null;
            SetTickSimulatedFor(node, tick);
        }

        Context.NetworkPerformance?.PushRollbackNodesSimulated(nodes.Count);
    }

    /// <summary>Nodes to simulate for the tick of <paramref name="inputSnapshot"/>, in scene tree order.</summary>
    internal List<Node> GetNodesToSimulate(Snapshot? inputSnapshot)
    {
        var result = new List<Node>();
        if (inputSnapshot is null) return result;

        var liveness = _livenessServer ?? Context.RollbackLivenessServer;
        var rollback = Context.NetworkRollback;
        var tick = inputSnapshot.Tick;

        foreach (var node in GetTree().GetNodesInGroup(_group))
        {
            if (!_callbacks.ContainsKey(node)) continue;
            if (!liveness.IsAlive(node, tick)) continue;

            var inputs = _inputGraph.GetLinkedTo(node);
            if (inputs.Count == 0)
            {
                // Node has no input, simulate it
                result.Add(node);
                continue;
            }

            if (rollback is not null && rollback.IsMutated(node, tick))
            {
                result.Add(node);
                continue;
            }

            if (!inputSnapshot.HasSubjects(inputs, requireAuth: true) && !IsPredictionEnabledFor(node))
            {
                // No input for the node and input prediction is disabled
                continue;
            }

            result.Add(node);
        }

        return result;
    }

    internal bool IsPredicting(Snapshot? inputSnapshot, Node node)
    {
        var inputNodes = _inputGraph.GetLinkedTo(node);
        var isOwned = node.IsMultiplayerAuthority();
        var isInputless = inputNodes.Count == 0;
        var hasInput = !isInputless && inputSnapshot is not null && inputSnapshot.HasSubjects(inputNodes, requireAuth: true);

        // We do not own the node, but we have input for it: not predicting
        if (!isOwned && hasInput) return false;
        // We do not own the node, so we can only guess
        if (!isOwned) return true;
        // We own the node and it does not depend on input
        if (isInputless) return false;
        // We own the node, it depends on input, and we have no input for it
        if (!hasInput) return true;
        return false;
    }

    private bool IsTickFreshFor(Node node, int tick)
        => !_simulatedTicks.TryGetValue(node, out var ticks) || !ticks.Contains(tick);

    private void SetTickSimulatedFor(Node node, int tick)
    {
        if (!_simulatedTicks.TryGetValue(node, out var ticks))
            _simulatedTicks[node] = ticks = new List<int>();
        ticks.Add(tick);
    }

    internal void TrimTicksSimulated(int beginning)
    {
        foreach (var ticks in _simulatedTicks.Values)
            ticks.RemoveAll(tick => tick < beginning);
    }

    internal IReadOnlyCollection<Node> GetControlledBy(Node input) => _inputGraph.GetLinkedFrom(input);

    internal IReadOnlyCollection<Node> GetInputsOf(Node node) => _inputGraph.GetLinkedTo(node);
}
