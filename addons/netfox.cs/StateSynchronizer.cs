using Godot;
using Netfox.Core.Logging;
using Netfox.Internal;

namespace Netfox;

/// <summary>Replicates properties from their authority to every peer once per tick, without rollback. Port of state-synchronizer.gd.</summary>
[Tool]
[GlobalClass]
[Icon("res://addons/netfox.cs/icons/state-synchronizer.svg")]
public partial class StateSynchronizer : Node
{
    private static readonly NetfoxLogger Logger = NetfoxLogger.ForNetfox("StateSynchronizer");

    /// <summary>Node the property paths are relative to; defaults to the parent.</summary>
    [Export] public Node? Root { get; set; }

    /// <summary>Property paths in "Node:property" form, relative to Root.</summary>
    [Export] public string[] Properties { get; set; } = [];

    /// <summary>Controls which peers receive state. Added as a child automatically.</summary>
    public PeerVisibilityFilter VisibilityFilter { get; set; } = new();

    private bool _propertiesDirty;
    private readonly PropertyPool _properties = new();
    private readonly HashSet<Node> _schemaNodes = new(ReferenceEqualityComparer.Instance);
    private bool _isInitialized;
    private Action<int>? _clientStartHandler;
    private bool _listensToMultiplayer;

    public bool IsInitialized => _isInitialized;

    public void ProcessSettings()
    {
        var root = Root ??= GetParent();
        var history = NetworkHistoryServer.Instance;
        var synchronization = NetworkSynchronizationServer.Instance;

        foreach (var node in _properties.Subjects)
            foreach (var property in _properties.GetPropertiesOf(node))
            {
                history.DeregisterSyncState(node, property);
                synchronization.DeregisterSyncState(node, property);
            }

        _properties.SetFromPaths(root, Properties);
        foreach (var node in _properties.Subjects)
        {
            synchronization.RegisterVisibilityFilter(node, VisibilityFilter);
            NetworkIdentityServer.Instance.RegisterNode(node);

            foreach (var property in _properties.GetPropertiesOf(node))
            {
                history.RegisterSyncState(node, property);
                synchronization.RegisterSyncState(node, property);
            }
        }

        _isInitialized = true;
    }

    /// <summary>Add a property at runtime. Node may be a string, NodePath or Node relative to Root.</summary>
    public void AddState(object node, string property)
    {
        var path = PropertyEntry.MakePath(Root ?? GetParent(), node, property);
        if (path.Length == 0 || Properties.Contains(path)) return;

        Properties = [.. Properties, path];
        _propertiesDirty = true;
        Callable.From(ReprocessSettings).CallDeferred();
    }

    public void SetSchema(IReadOnlyDictionary<string, NetworkSchemaSerializer> schema)
    {
        ClearSchema();
        MergeSchema(schema);
    }

    public void MergeSchema(IReadOnlyDictionary<string, NetworkSchemaSerializer> schema)
    {
        var root = Root ?? GetParent();
        foreach (var (path, serializer) in schema)
        {
            var entry = PropertyEntry.Parse(root, path);
            NetworkSynchronizationServer.Instance.RegisterSchema(entry.Node, entry.Property, serializer);
            _schemaNodes.Add(entry.Node);
        }
    }

    public void ClearSchema()
    {
        foreach (var node in _schemaNodes)
            NetworkSynchronizationServer.Instance.DeregisterSchemaFor(node);
        _schemaNodes.Clear();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationEditorPreSave) UpdateConfigurationWarnings();
    }

    public override string[] _GetConfigurationWarnings()
    {
        Root ??= GetParent();
        if (Root is null) return ["No valid root node found!"];

        return EditorUtils.GatherProperties<ISynchronizedStateProperties>(Root, n => n.GetSynchronizedStateProperties(), AddState);
    }

    public override void _EnterTree()
    {
        if (Engine.IsEditorHint()) return;

        VisibilityFilter ??= new PeerVisibilityFilter();
        if (VisibilityFilter.GetParent() is null)
            AddChild(VisibilityFilter);
    }

    public override void _ExitTree()
    {
        if (Engine.IsEditorHint()) return;

        if (_clientStartHandler is not null && NetworkEvents.Instance is { } events)
            events.OnClientStart -= _clientStartHandler;
        if (_listensToMultiplayer && GodotObject.IsInstanceValid(Multiplayer)) Multiplayer.ConnectedToServer -= ProcessSettings;

        foreach (var node in _properties.Subjects.ToList())
        {
            NetworkSynchronizationServer.Instance?.Deregister(node);
            NetworkHistoryServer.Instance?.Deregister(node);
            NetworkIdentityServer.Instance?.DeregisterNode(node);
        }
    }

    public override void _Ready()
    {
        if (Engine.IsEditorHint()) return;

        Callable.From(ProcessSettings).CallDeferred();

        // Reprocess on connect: pre-placed nodes start owned by us (offline peer 1), then change owner
        if (NetworkEvents.Instance is { Enabled: true } events)
        {
            _clientStartHandler = _ => ProcessSettings();
            events.OnClientStart += _clientStartHandler;
        }
        else
        {
            Multiplayer.ConnectedToServer += ProcessSettings;
            _listensToMultiplayer = true;
        }
    }

    private void ReprocessSettings()
    {
        if (!_propertiesDirty) return;
        _propertiesDirty = false;
        ProcessSettings();
    }
}
