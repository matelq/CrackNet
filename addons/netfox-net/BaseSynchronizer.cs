using Godot;
using Netfox.Internal;

namespace Netfox;

/// <summary>
/// Plumbing shared by <see cref="RollbackSynchronizer"/>, <see cref="PredictiveSynchronizer"/> and
/// <see cref="StateSynchronizer"/>: context resolution, deferred reprocessing, reprocess-on-connect and its teardown,
/// managed node discovery and schema registration.
/// <para>
/// Upstream has no such base; rollback-synchronizer.gd, predictive-synchronizer.gd and state-synchronizer.gd repeat all
/// of it. Only the parts that are identical in every synchronizer live here, and nothing is added to a synchronizer's
/// public API by inheriting: the extras stay protected.
/// </para>
/// </summary>
public abstract partial class BaseSynchronizer : Node
{
    /// <summary>The netfox stack this node uses; resolved when it enters the tree.</summary>
    public NetfoxContext Context { get; private set; } = NetfoxContext.Default;

    private bool _propertiesDirty;
    private Action<int>? _clientStartHandler;
    private Action? _serverStartHandler;
    private bool _listensToMultiplayer;
    private Action? _sessionResetHandler;
    private readonly HashSet<Node> _schemaNodes = new(ReferenceEqualityComparer.Instance);

    /// <summary>(Re)registers everything this synchronizer manages with the servers.</summary>
    public abstract void ProcessSettings();

    /// <summary>The node property paths are relative to: the synchronizer's Root, defaulting to the parent.</summary>
    protected abstract Node ResolveRoot();

    /// <summary>True when another synchronizer of the same kind treats <paramref name="node"/> as its root.</summary>
    protected virtual bool IsForeignRoot(Node node) => false;

    public override void _EnterTree()
    {
        Context = NetfoxContext.For(this);

        _sessionResetHandler = OnSessionReset;
        Context.SessionReset += _sessionResetHandler;
    }

    /// <summary>
    /// The servers have dropped the ticks of the session that just ended, so everything this synchronizer registered
    /// has to be registered again, at the new session's ticks.
    /// </summary>
    protected virtual void OnSessionReset()
    {
        if (Engine.IsEditorHint() || !IsInsideTree()) return;
        ProcessSettings();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationEditorPreSave) UpdateConfigurationWarnings();
    }

    /// <summary>Queues a deferred <see cref="ProcessSettings"/>; call after adding property paths at runtime.</summary>
    protected void MarkPropertiesDirty()
    {
        _propertiesDirty = true;
        Callable.From(ReprocessSettings).CallDeferred();
    }

    private void ReprocessSettings()
    {
        if (!_propertiesDirty || Engine.IsEditorHint()) return;
        _propertiesDirty = false;
        ProcessSettings();
    }

    /// <summary>
    /// Processes settings again once the peer connects: pre-placed nodes start owned by us (the offline peer 1) and
    /// only then change owner. The user may swap out <c>multiplayer</c>, which NetworkEvents already handles.
    /// </summary>
    protected void ReprocessOnConnect()
    {
        if (Context.NetworkEvents is { Enabled: true } events)
        {
            _clientStartHandler = _ => ProcessSettings();
            events.OnClientStart += _clientStartHandler;

            // Upstream only reprocesses on the client (rollback-synchronizer.gd:_ready). The host needs it too: between
            // sessions it has no peer, and without one every node looks like someone else's, so nothing is owned
            _serverStartHandler = ProcessSettings;
            events.OnServerStart += _serverStartHandler;
        }
        else
        {
            Multiplayer.ConnectedToServer += ProcessSettings;
            _listensToMultiplayer = true;
        }
    }

    /// <summary>
    /// Undoes <see cref="ReprocessOnConnect"/>. Godot disconnects the signals of a freed node on its own, C# events are
    /// not, so every synchronizer must do this when it leaves the tree.
    /// </summary>
    protected void StopReprocessOnConnect()
    {
        if (_sessionResetHandler is not null) Context.SessionReset -= _sessionResetHandler;
        _sessionResetHandler = null;

        if (Context.NetworkEvents is { } events)
        {
            if (_clientStartHandler is not null) events.OnClientStart -= _clientStartHandler;
            if (_serverStartHandler is not null) events.OnServerStart -= _serverStartHandler;
        }
        _clientStartHandler = null;
        _serverStartHandler = null;

        if (_listensToMultiplayer && GodotObject.IsInstanceValid(Multiplayer))
            Multiplayer.ConnectedToServer -= ProcessSettings;
        _listensToMultiplayer = false;
    }

    /// <summary>Replaces the schema of every property registered so far.</summary>
    protected void SetSchemaInternal(IReadOnlyDictionary<string, NetworkSchemaSerializer> schema)
    {
        ClearSchemaInternal();
        MergeSchemaInternal(schema);
    }

    /// <summary>Registers serializers for the given "Node:property" paths, keeping the ones already set.</summary>
    protected void MergeSchemaInternal(IReadOnlyDictionary<string, NetworkSchemaSerializer> schema)
    {
        var root = ResolveRoot();
        foreach (var (path, serializer) in schema)
        {
            var entry = PropertyEntry.Parse(root, path);
            Context.NetworkSynchronizationServer.RegisterSchema(entry.Node, entry.Property, serializer);
            _schemaNodes.Add(entry.Node);
        }
    }

    /// <summary>Drops every serializer registered through this synchronizer, falling back to variant encoding.</summary>
    protected void ClearSchemaInternal()
    {
        foreach (var node in _schemaNodes)
            Context.NetworkSynchronizationServer.DeregisterSchemaFor(node);
        _schemaNodes.Clear();
    }

    /// <summary>Every descendant of <paramref name="root"/>, except branches rooted by another synchronizer.</summary>
    protected List<Node> CollectManagedNodes(Node root)
    {
        var result = new List<Node>();
        foreach (var child in root.GetChildren())
        {
            if (IsForeignRoot(child)) continue;
            result.Add(child);
            result.AddRange(CollectManagedNodes(child));
        }
        return result;
    }
}
