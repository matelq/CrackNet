using Godot;
using Netfox.Internal;

namespace Netfox;

/// <summary>Replicates properties from their authority to every peer once per tick, without rollback. Port of state-synchronizer.gd.</summary>
[Tool]
[GlobalClass]
[Icon("res://addons/netfox-net/icons/state-synchronizer.svg")]
public partial class StateSynchronizer : BaseSynchronizer
{
    /// <summary>Node the property paths are relative to; defaults to the parent.</summary>
    [Export] public Node? Root { get; set; }

    /// <summary>Property paths in "Node:property" form, relative to Root.</summary>
    [Export] public string[] Properties { get; set; } = [];

    /// <summary>Controls which peers receive state. Added as a child automatically.</summary>
    public PeerVisibilityFilter VisibilityFilter { get; set; } = new();

    private readonly PropertyPool _properties = new();
    private bool _isInitialized;

    public bool IsInitialized => _isInitialized;

    protected override Node ResolveRoot() => Root ??= GetParent();

    public override void ProcessSettings()
    {
        var root = ResolveRoot();
        var history = Context.NetworkHistoryServer;
        var synchronization = Context.NetworkSynchronizationServer;

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
            Context.NetworkIdentityServer.RegisterNode(node);

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
        MarkPropertiesDirty();
    }

    public void SetSchema(IReadOnlyDictionary<string, NetworkSchemaSerializer> schema) => SetSchemaInternal(schema);

    public void MergeSchema(IReadOnlyDictionary<string, NetworkSchemaSerializer> schema) => MergeSchemaInternal(schema);

    public void ClearSchema() => ClearSchemaInternal();

    public override string[] _GetConfigurationWarnings()
    {
        Root ??= GetParent();
        if (Root is null) return ["No valid root node found!"];

        return EditorUtils.GatherProperties<ISynchronizedStateProperties>(Root, n => n.GetSynchronizedStateProperties(), AddState);
    }

    public override void _EnterTree()
    {
        base._EnterTree();
        if (Engine.IsEditorHint()) return;

        VisibilityFilter ??= new PeerVisibilityFilter();
        if (VisibilityFilter.GetParent() is null)
            AddChild(VisibilityFilter);
    }

    public override void _ExitTree()
    {
        if (Engine.IsEditorHint()) return;

        StopReprocessOnConnect();

        foreach (var node in _properties.Subjects.ToList())
        {
            Context.NetworkSynchronizationServer?.Deregister(node);
            Context.NetworkHistoryServer?.Deregister(node);
            Context.NetworkIdentityServer?.DeregisterNode(node);
        }
    }

    public override void _Ready()
    {
        if (Engine.IsEditorHint()) return;

        Callable.From(ProcessSettings).CallDeferred();
        ReprocessOnConnect();
    }
}
