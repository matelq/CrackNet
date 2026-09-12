using Godot;
using Netfox.Extras;

namespace Netfox.Tests;

/// <summary>Deterministic input: each peer pushes its own player along a fixed axis, so the two are distinguishable.</summary>
public partial class HarnessInput : BaseNetInput
{
    [Export] public Vector3 Movement { get; set; }

    protected override void Gather() => Movement = GetMultiplayerAuthority() == 1 ? Vector3.Right : Vector3.Back;
}

/// <summary>
/// A player whose position is rollback state driven by its input child, mirroring examples/e2e. Both stacks build the
/// same subtree under the same name, which is what makes their identities line up.
/// </summary>
public partial class HarnessPlayer : Node3D, IRollbackTick
{
    public const float Speed = 4.0f;

    public HarnessInput Input { get; private set; } = null!;
    public RollbackSynchronizer Synchronizer { get; private set; } = null!;
    public int SimulatedTicks { get; private set; }

    public static HarnessPlayer Spawn(Node parent, int ownerPeer, bool enablePrediction = false)
    {
        var player = new HarnessPlayer { Name = $"Player_{ownerPeer}" };
        player.SetMultiplayerAuthority(1);

        player.Input = new HarnessInput { Name = "Input" };
        player.Input.SetMultiplayerAuthority(ownerPeer);
        player.AddChild(player.Input);

        player.Synchronizer = new RollbackSynchronizer
        {
            Name = "RollbackSynchronizer",
            Root = player,
            StateProperties = [":position"],
            InputProperties = ["Input:Movement"],
            EnablePrediction = enablePrediction,
        };
        player.AddChild(player.Synchronizer);

        parent.AddChild(player);
        return player;
    }

    public void RollbackTick(double delta, int tick, bool isFresh)
    {
        Position += Input.Movement * Speed * (float)delta;
        SimulatedTicks++;
    }
}
