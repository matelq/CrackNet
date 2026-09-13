using Godot;
using Netfox.Internal;

namespace Netfox;

/// <summary>Smooths the configured properties between network ticks. Port of tick-interpolator.gd.</summary>
[Tool]
[GlobalClass]
[Icon("res://addons/netfox-net/icons/tick-interpolator.svg")]
public partial class TickInterpolator : Node
{
    /// <summary>The netfox stack this node uses; resolved when it enters the tree.</summary>
    public NetfoxContext Context { get; private set; } = NetfoxContext.Default;

    /// <summary>Node the property paths are relative to; defaults to the parent.</summary>
    [Export] public Node? Root { get; set; }

    [Export] public bool Enabled { get; set; } = true;

    /// <summary>Property paths in "Node:property" form, relative to Root.</summary>
    [Export] public string[] Properties { get; set; } = [];

    /// <summary>Snap to the first recorded state instead of interpolating from defaults.</summary>
    [Export] public bool RecordFirstState { get; set; } = true;

    /// <summary>Record state automatically after every tick loop.</summary>
    [Export] public bool EnableRecording { get; set; } = true;

    private bool _propertiesDirty;
    private readonly PropertyPool _properties = new();

    public void ProcessSettings()
    {
        Root ??= GetParent();
        var server = Context.InterpolationServer;

        foreach (var subject in _properties.Subjects.ToList())
            server.Deregister(subject);

        _properties.SetFromPaths(Root, Properties);
        foreach (var subject in _properties.Subjects)
        {
            foreach (var property in _properties.GetPropertiesOf(subject))
                server.Register(subject, property);

            server.SetEnabled(subject, Enabled);
            server.SetRecording(subject, EnableRecording);
        }
    }

    /// <summary>Add a property at runtime. Node may be a string, NodePath or Node relative to Root.</summary>
    public void AddProperty(object node, string property)
    {
        var path = PropertyEntry.MakePath(Root ?? GetParent(), node, property);
        if (path.Length == 0 || Properties.Contains(path)) return;

        Properties = [.. Properties, path];
        _propertiesDirty = true;
        Callable.From(ReprocessSettings).CallDeferred();
    }

    public bool CanInterpolate()
    {
        foreach (var subject in _properties.Subjects)
            if (!Context.InterpolationServer.CanInterpolate(subject)) return false;
        return true;
    }

    public void PushState()
    {
        foreach (var subject in _properties.Subjects)
            Context.InterpolationServer.PushState(subject);
    }

    /// <summary>Skip interpolation for the next tick loop, e.g. after respawning.</summary>
    public void Teleport()
    {
        foreach (var subject in _properties.Subjects)
            Context.InterpolationServer.Teleport(subject);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationEditorPreSave) UpdateConfigurationWarnings();
    }

    public override string[] _GetConfigurationWarnings()
    {
        Root ??= GetParent();
        if (Root is null) return ["No valid root node found!"];

        return EditorUtils.GatherProperties<IInterpolatedProperties>(Root, n => n.GetInterpolatedProperties(), AddProperty);
    }

    public override async void _EnterTree()
    {
        Context = NetfoxContext.For(this);
        if (Engine.IsEditorHint()) return;

        Callable.From(ProcessSettings).CallDeferred();

        // Wait a frame for any initial setup before recording the first state
        if (RecordFirstState)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (IsInsideTree()) Teleport();
        }
    }

    public override void _ExitTree()
    {
        if (Engine.IsEditorHint()) return;

        foreach (var subject in _properties.Subjects.ToList())
            Context.InterpolationServer?.Deregister(subject);
    }

    private void ReprocessSettings()
    {
        if (!_propertiesDirty || Engine.IsEditorHint()) return;
        _propertiesDirty = false;
        ProcessSettings();
    }
}
