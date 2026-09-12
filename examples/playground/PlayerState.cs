using Godot;
using Netfox.Extras;

namespace Netfox.Examples.Playground;

/// <summary>
/// Base for the player's states. A state is a child of the <see cref="RewindableStateMachine"/>, which is itself a
/// child of the player, so the player is two levels up.
/// <para>
/// The machine's current state is rollback state like any other property, so a rewind puts the player back in the
/// state it was in for that tick and replays the transitions from there. That is the whole reason this exists rather
/// than an ordinary state machine: a normal one would keep whatever state the mispredicted future left it in.
/// </para>
/// </summary>
public abstract partial class PlayerState : RewindableState
{
    protected PlayerCharacter Player => field ??= GetParent().GetParent<PlayerCharacter>();

    protected PlayerInput Input => Player.Input;
}
