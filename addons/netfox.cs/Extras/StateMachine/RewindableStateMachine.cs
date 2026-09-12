using Godot;
using Netfox.Core.Logging;

namespace Netfox.Extras;

/// <summary>
/// State machine whose current state is rollback state: list its State property on a sibling RollbackSynchronizer.
/// Child RewindableState nodes are the available states. Port of netfox.extras/state-machine/rewindable-state-machine.gd.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/netfox.cs/icons/rewindable-state-machine.svg")]
public partial class RewindableStateMachine : Node, IRollbackTick
{
    private static readonly NetfoxLogger Logger = NetfoxLogger.ForExtras("RewindableStateMachine");

    /// <summary>Name of the current state. Set it to jump without running transition callbacks; use Transition() otherwise.</summary>
    [Export]
    public StringName State
    {
        get => _stateObject is not null ? _stateObject.Name : new StringName("");
        set => SetState(value);
    }

    /// <summary>(old state, new state)</summary>
    public event Action<RewindableState?, RewindableState>? OnStateChanged;
    /// <summary>(old state, new state). Emitted after the tick loop when the shown state changed.</summary>
    public event Action<RewindableState?, RewindableState>? OnDisplayStateChanged;

    private RewindableState? _stateObject;
    private RewindableState? _previousStateObject;
    private readonly Dictionary<string, RewindableState> _availableStates = new();
    private bool _preventTransition;

    /// <summary>Attempt to transition; returns false if refused by CanEnter, OnExit or OnEnter handlers.</summary>
    public bool Transition(StringName newStateName)
    {
        var name = newStateName.ToString();
        if (State == newStateName) return false;

        if (!_availableStates.TryGetValue(name, out var newState))
        {
            Logger.Warning("Attempted to transition from state {0} into unknown state {1}", State, newStateName);
            return false;
        }

        var fromState = _stateObject;
        var tick = NetworkRollback.Instance.Tick;
        _preventTransition = false;
        Action prevent = () => _preventTransition = true;

        if (fromState is not null)
        {
            if (!newState.CanEnter(fromState)) return false;

            fromState.EmitExit(newState, tick, prevent);
            if (_preventTransition) return false;
        }

        newState.EmitEnter(fromState, tick, prevent);
        if (_preventTransition) return false;

        if (fromState is not null && GodotObject.IsInstanceValid(fromState))
            fromState.Exit(newState, tick);
        newState.Enter(fromState, tick);

        _stateObject = newState;
        OnStateChanged?.Invoke(fromState, newState);
        return true;
    }

    /// <summary>Re-reads the child RewindableState nodes.</summary>
    public void UpdateStates()
    {
        _availableStates.Clear();
        foreach (var child in GetChildren())
            if (child is RewindableState state)
                _availableStates[state.Name] = state;
    }

    public void RollbackTick(double delta, int tick, bool isFresh)
    {
        if (_stateObject is null) return;
        _stateObject.Tick(delta, tick, isFresh);
        _stateObject.EmitTick(delta, tick, isFresh);
    }

    public override void _Notification(int what)
    {
        if (Engine.IsEditorHint()) return;

        switch ((long)what)
        {
            case NotificationChildOrderChanged:
                UpdateStates();
                break;
            case NotificationEnterTree:
                NetworkTime.Instance.AfterTickLoop += AfterTickLoop;
                UpdateStates();
                break;
            case NotificationExitTree:
                if (NetworkTime.Instance is not null) NetworkTime.Instance.AfterTickLoop -= AfterTickLoop;
                break;
        }
    }

    public override string[] _GetConfigurationWarnings()
    {
        const string missingSynchronizer = "RewindableStateMachine is not managed by a RollbackSynchronizer! Add it as a sibling node to fix this.";
        const string invalidConfig = "RollbackSynchronizer configuration is invalid, it cannot manage this state machine!" +
            "\nNote: You may need to reload this scene after fixing for this warning to disappear.";
        const string missingProperty = "State is not managed by RollbackSynchronizer! Add the `State` property to the synchronizer to fix this. " +
            "\nNote: You may need to reload this scene after fixing for this warning to disappear.";

        var parent = GetParent();
        var synchronizer = parent?.GetChildren().OfType<RollbackSynchronizer>().FirstOrDefault();
        if (synchronizer is null) return [missingSynchronizer];
        if (synchronizer.Root is null) return [invalidConfig];

        foreach (var path in synchronizer.StateProperties)
        {
            var entry = PropertyEntry.Parse(synchronizer.Root, path);
            if (entry.Node == this && entry.Property.ToString() == nameof(State))
                return [];
        }

        return [missingProperty];
    }

    private void AfterTickLoop()
    {
        if (_stateObject == _previousStateObject || _stateObject is null) return;

        var tick = NetworkTime.Instance.Tick;
        OnDisplayStateChanged?.Invoke(_previousStateObject, _stateObject);

        if (_previousStateObject is not null)
        {
            _previousStateObject.EmitDisplayExit(_stateObject, tick);
            _previousStateObject.DisplayExit(_stateObject, tick);
        }

        _stateObject.EmitDisplayEnter(_previousStateObject, tick);
        _stateObject.DisplayEnter(_previousStateObject, tick);

        _previousStateObject = _stateObject;
    }

    private void SetState(StringName newState)
    {
        var name = newState.ToString();
        if (name.Length == 0) return;

        if (!_availableStates.TryGetValue(name, out var state))
        {
            Logger.Warning("Attempted to jump to unknown state: {0}", newState);
            return;
        }

        _stateObject = state;
    }
}
