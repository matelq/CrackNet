using Godot;
using Netfox.Core.Logging;
using Netfox.Internal;

namespace Netfox;

/// <summary>Keeps from/to snapshots per subject and interpolates them every rendered frame. Port of servers/interpolation-server.gd.</summary>
public partial class InterpolationServer : Node
{
    public static InterpolationServer Instance { get; private set; } = null!;

    /// <summary>The stack this server belongs to; resolved when it enters the tree.</summary>
    public NetfoxContext Context { get; private set; } = NetfoxContext.Default;

    private static readonly NetfoxLogger Logger = NetfoxLogger.ForNetfox("InterpolationServer");

    private bool _enabled = true;

    private readonly PropertyPool _properties = new();
    private readonly Dictionary<Node, Dictionary<NodePath, Interpolators.Interpolator>> _interpolators = new(ReferenceEqualityComparer.Instance);

    private readonly Snapshot _stateFrom = new(0);
    private readonly Snapshot _stateTo = new(0);

    private readonly HashSet<Node> _enabledSubjects = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Node> _recordingEnabled = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Node> _teleporting = new(ReferenceEqualityComparer.Instance);

    public override void _EnterTree()
    {
        NetfoxRuntime.EnsureInitialized();
        Context = NetfoxContext.For(this);
        Context.InterpolationServer ??= this;
        if (Context.IsDefault) Instance ??= this;
    }

    public override void _Ready()
    {
        // Disabled by default when headless
        _enabled = DisplayServer.GetName() != "headless";
    }

    public override void _ExitTree()
    {
        if (ReferenceEquals(Context.InterpolationServer, this)) Context.InterpolationServer = null!;
        if (Instance == this) Instance = null!;
    }

    /// <summary>Register a property for interpolation. The interpolator defaults to one matching the current value type.</summary>
    public void Register(Node subject, NodePath property, Interpolators.Interpolator? interpolator = null)
    {
        if (!IsServerEnabled()) return;

        if (!_properties.HasSubject(subject))
        {
            _interpolators[subject] = new Dictionary<NodePath, Interpolators.Interpolator>();
            _enabledSubjects.Add(subject);
            _recordingEnabled.Add(subject);
        }

        if (_properties.Has(subject, property)) return;

        _properties.Add(subject, property);
        _interpolators[subject][property] = interpolator ?? Interpolators.FindInterpolatorFor(subject.GetValue(property));
    }

    public void Deregister(Node subject)
    {
        if (!IsServerEnabled()) return;

        _stateFrom.EraseSubject(subject);
        _stateTo.EraseSubject(subject);
        _properties.EraseSubject(subject);
        _interpolators.Remove(subject);
        _enabledSubjects.Remove(subject);
        _recordingEnabled.Remove(subject);
        _teleporting.Remove(subject);
    }

    public bool HasSubject(Node subject) => _properties.HasSubject(subject);

    public void SetEnabled(Node subject, bool enabled)
    {
        if (!IsServerEnabled()) return;
        if (enabled) _enabledSubjects.Add(subject);
        else _enabledSubjects.Remove(subject);
    }

    public bool IsEnabled(Node subject) => _enabledSubjects.Contains(subject);

    public void SetRecording(Node subject, bool enabled)
    {
        if (!IsServerEnabled()) return;
        if (enabled) _recordingEnabled.Add(subject);
        else _recordingEnabled.Remove(subject);
    }

    public bool IsRecording(Node subject) => _recordingEnabled.Contains(subject);

    public bool CanInterpolate(Node subject)
    {
        if (!IsServerEnabled()) return false;
        if (!HasSubject(subject)) return false;
        if (!IsEnabled(subject)) return false;
        if (IsTeleporting(subject)) return false;
        return true;
    }

    /// <summary>Global toggle; off by default in headless mode.</summary>
    public void SetServerEnabled(bool enabled) => _enabled = enabled;

    public bool IsServerEnabled() => _enabled;

    /// <summary>Rotate states: the previous target becomes the source, the current values become the target.</summary>
    public void PushState(Node subject)
    {
        if (!IsServerEnabled()) return;

        if (!HasSubject(subject))
        {
            Logger.Warning("Trying to push state for unregistered subject {0}", subject);
            return;
        }

        _stateTo.CopySubjectTo(subject, _stateFrom);

        _stateTo.EraseSubject(subject);
        foreach (var property in _properties.GetPropertiesOf(subject))
        {
            var value = subject.GetValue(property);
            if (value.VariantType == Variant.Type.Nil)
                Logger.Warning("Captured null value for interpolation on {0}:{1}; either a bug or wrong usage", subject, property);
            else
                _stateTo.SetProperty(subject, property, value);
        }
    }

    /// <summary>Skip interpolation for the subject until the next tick loop.</summary>
    public void Teleport(Node subject)
    {
        if (!IsServerEnabled()) return;
        if (IsTeleporting(subject)) return;

        if (!HasSubject(subject))
        {
            Logger.Warning("Trying to teleport unregistered subject {0}", subject);
            return;
        }

        _teleporting.Add(subject);
    }

    public bool IsTeleporting(Node subject) => _teleporting.Contains(subject);

    public void InterpolateSubject(Node subject, double factor)
    {
        if (!IsServerEnabled()) return;
        if (!CanInterpolate(subject)) return;

        var interps = _interpolators.GetValueOrDefault(subject);
        if (interps is null || interps.Count == 0)
            Logger.Debug("No interpolators found for {0}", subject);

        foreach (var property in _properties.GetPropertiesOf(subject))
        {
            if (!_stateFrom.TryGetProperty(subject, property, out var a)) continue;
            if (!_stateTo.TryGetProperty(subject, property, out var b)) continue;

            var interpolator = interps is not null && interps.TryGetValue(property, out var found) ? found : Interpolators.DefaultInterpolator;
            subject.SetValue(property, interpolator.Apply(a, b, factor));
        }
    }

    public void Interpolate(double factor)
    {
        foreach (var subject in _properties.Subjects.ToList())
            InterpolateSubject(subject, factor);
    }

    internal void ClearTeleports() => _teleporting.Clear();

    internal void ApplyTargetState() => _stateTo.Apply();

    internal void RecordNextState()
    {
        foreach (var subject in _recordingEnabled.ToList())
            PushState(subject);
    }
}
