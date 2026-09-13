using Godot;
using Netfox.Extras;

namespace Netfox.Examples.Playground;

/// <summary>In the air: gravity, air control, and the remaining jumps.</summary>
[GlobalClass]
public partial class AirborneState : PlayerState
{
    public override void Tick(double delta, int tick, bool isFresh)
    {
        Player.RefreshIsOnFloor();
        Player.RideFloor(tick);
        Player.Carry(tick);

        var velocity = Player.Velocity;
        velocity.Y -= Player.Gravity * (float)delta;

        if (Player.ConsumeJump() && Player.JumpsLeft > 0)
        {
            velocity.Y = Player.JumpVelocity;
            Player.JumpsLeft--;
        }

        Player.Move(Player.WithInput(velocity), delta);

        if (Player.IsOnFloor()) StateMachine?.Transition("Grounded");
    }
}
