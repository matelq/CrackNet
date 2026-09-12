using Godot;
using Netfox.Extras;

namespace Netfox.Examples.Playground;

/// <summary>On the ground: full control, jumps replenished, and jumping is what leaves.</summary>
[GlobalClass]
public partial class GroundedState : PlayerState
{
    public override void Tick(double delta, int tick, bool isFresh)
    {
        Player.RefreshIsOnFloor();
        Player.RideFloor(tick);

        var velocity = Player.Velocity;
        Player.JumpsLeft = Player.MaxJumps;
        velocity.Y = Mathf.Max(velocity.Y, 0);

        if (Player.ConsumeJump())
        {
            velocity.Y = Player.JumpVelocity;
            Player.JumpsLeft--;
            Player.Move(Player.WithInput(velocity), delta);
            StateMachine?.Transition("Airborne");
            return;
        }

        Player.Move(Player.WithInput(velocity), delta);

        // Walked off an edge rather than jumped
        if (!Player.IsOnFloor()) StateMachine?.Transition("Airborne");
    }
}
