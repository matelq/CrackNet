using Godot;

namespace Netfox.Extras;

/// <summary>A state of a RewindableStateMachine. Override the virtual methods or subscribe to the events. Port of netfox.extras/state-machine/rewindable-state.gd.</summary>
[Tool]
[GlobalClass]
[Icon("res://addons/netfox-net/icons/rewindable-state.svg")]
public partial class RewindableState : Node
{
    /// <summary>(previous state, tick, prevent). Call prevent to cancel the transition.</summary>
    public event Action<RewindableState?, int, Action>? OnEnter;
    /// <summary>(delta, tick, isFresh)</summary>
    public event Action<double, int, bool>? OnTick;
    /// <summary>(next state, tick, prevent). Call prevent to cancel the transition.</summary>
    public event Action<RewindableState, int, Action>? OnExit;
    /// <summary>(previous state, tick). Emitted once the state is shown on screen.</summary>
    public event Action<RewindableState?, int>? OnDisplayEnter;
    /// <summary>(next state, tick). Emitted once the state is no longer shown on screen.</summary>
    public event Action<RewindableState, int>? OnDisplayExit;

    public RewindableStateMachine? StateMachine { get; private set; }

    public virtual void Tick(double delta, int tick, bool isFresh) { }

    public virtual void Enter(RewindableState? previousState, int tick) { }

    public virtual void Exit(RewindableState nextState, int tick) { }

    /// <summary>Return false to refuse the transition into this state.</summary>
    public virtual bool CanEnter(RewindableState? previousState) => true;

    public virtual void DisplayEnter(RewindableState? previousState, int tick) { }

    public virtual void DisplayExit(RewindableState nextState, int tick) { }

    internal void EmitEnter(RewindableState? previous, int tick, Action prevent) => OnEnter?.Invoke(previous, tick, prevent);
    internal void EmitTick(double delta, int tick, bool isFresh) => OnTick?.Invoke(delta, tick, isFresh);
    internal void EmitExit(RewindableState next, int tick, Action prevent) => OnExit?.Invoke(next, tick, prevent);
    internal void EmitDisplayEnter(RewindableState? previous, int tick) => OnDisplayEnter?.Invoke(previous, tick);
    internal void EmitDisplayExit(RewindableState next, int tick) => OnDisplayExit?.Invoke(next, tick);

    public override string[] _GetConfigurationWarnings()
        => GetParent() is RewindableStateMachine ? [] : ["This state should be a child of a RewindableStateMachine."];

    public override void _Notification(int what)
    {
        // Notification instead of _Ready, so subclasses can override _Ready without calling base
        if (what == NotificationReady && StateMachine is null && GetParent() is RewindableStateMachine machine)
            StateMachine = machine;
    }
}
