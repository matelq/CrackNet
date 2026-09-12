using Godot;
using Netfox.Extras;

namespace Netfox.Tests;

/// <summary>Deterministic input: each peer pushes its own player along a fixed axis, so the two are distinguishable.</summary>
public partial class HarnessInput : BaseNetInput
{
    [Export] public Vector3 Movement { get; set; }

    /// <summary>Stands in for a held button: clearing it is what "the player let go" looks like to the netcode.</summary>
    public bool Held { get; set; } = true;

    protected override void Gather()
        => Movement = !Held ? Vector3.Zero : GetMultiplayerAuthority() == 1 ? Vector3.Right : Vector3.Back;
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

    /// <summary>Ticks simulated without up to date input, so a test can tell whether prediction was in play at all.</summary>
    public int PredictedTicks { get; private set; }

    /// <summary>A second input node owned by the host, for upstream foxssake/netfox#236. Contributes no movement.</summary>
    public HarnessInput? Events { get; private set; }

    public static HarnessPlayer Spawn(Node parent, int ownerPeer, bool enablePrediction = false, bool withServerEvents = false)
    {
        var player = new HarnessPlayer { Name = $"Player_{ownerPeer}" };
        player.SetMultiplayerAuthority(1);

        player.Input = new HarnessInput { Name = "Input" };
        player.Input.SetMultiplayerAuthority(ownerPeer);
        player.AddChild(player.Input);

        var inputProperties = new List<string> { "Input:Movement" };
        if (withServerEvents)
        {
            player.Events = new HarnessInput { Name = "Events", Held = false };
            player.Events.SetMultiplayerAuthority(1);
            player.AddChild(player.Events);
            inputProperties.Add("Events:Movement");
        }

        player.Synchronizer = new RollbackSynchronizer
        {
            Name = "RollbackSynchronizer",
            Root = player,
            StateProperties = [":position"],
            InputProperties = [.. inputProperties],
            EnablePrediction = enablePrediction,
        };
        player.AddChild(player.Synchronizer);

        parent.AddChild(player);
        return player;
    }

    public void RollbackTick(double delta, int tick, bool isFresh)
    {
        Position += (Input.Movement + (Events?.Movement ?? Vector3.Zero)) * Speed * (float)delta;
        SimulatedTicks++;
        if (Synchronizer.IsPredicting()) PredictedTicks++;
    }
}
