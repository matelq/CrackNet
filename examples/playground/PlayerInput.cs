using Godot;
using Netfox.Extras;

namespace Netfox.Examples.Playground;

/// <summary>
/// The player's input for one tick. <see cref="BaseNetInput.Gather"/> runs once per tick loop and only on the peer
/// that owns this node; netfox records what it leaves here and sends it to whoever simulates the player.
/// <para>
/// The properties carry <c>[RollbackInput]</c>, so the synchronizer above picks them up by name from the generator
/// rather than from a string typed into the inspector.
/// </para>
/// </summary>
[GlobalClass]
public partial class PlayerInput : BaseNetInput
{
    [RollbackInput] public Vector2 Movement { get; set; }

    [RollbackInput] public bool Jump { get; set; }

    /// <summary>Pick up the nearest crate. Tab, because the sample sticks to Godot's built-in ui_* actions.</summary>
    [RollbackInput] public bool Grab { get; set; }

    /// <summary>Throw whatever is held. Shift+Tab.</summary>
    [RollbackInput] public bool Throw { get; set; }

    /// <summary>
    /// Firing is not rollback input in the way movement is - the weapon toolkit is request-and-accept, not rollback -
    /// but gathering it here keeps all the player's input in one place, and on the peer that owns it.
    /// </summary>
    public bool Fire { get; private set; }

    protected override void Gather()
    {
        Movement = Godot.Input.GetVector("ui_left", "ui_right", "ui_up", "ui_down");
        Jump = Godot.Input.IsActionPressed("ui_accept");
        Grab = Godot.Input.IsActionPressed("ui_focus_next");
        Throw = Godot.Input.IsActionPressed("ui_focus_prev");
        Fire = Godot.Input.IsActionPressed("ui_select");
    }
}
