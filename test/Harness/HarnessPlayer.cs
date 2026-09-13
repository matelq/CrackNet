using Godot;
using Netfox.Extras;

namespace Netfox.Tests;

/// <summary>Deterministic input: each peer pushes its own player along a fixed axis, so they stay distinguishable.</summary>
public partial class HarnessInput : BaseNetInput
{
    [Export] public Vector3 Movement { get; set; }

    /// <summary>
    /// Where this player walks while held. Only the owning peer ever reads it, since only the owner gathers - but
    /// every stack sets the same value for the same peer, so a trace stays readable from any of them.
    /// </summary>
    public Vector3 Direction { get; set; } = Vector3.Right;

    /// <summary>Stands in for a held button: clearing it is what "the player let go" looks like to the netcode.</summary>
    public bool Held { get; set; } = true;

    /// <summary>
    /// A distinct axis per peer, so a position says on its own whose player it is. Peers 1 and 2 keep the directions
    /// they had when the harness was two peers wide.
    /// </summary>
    public static Vector3 DirectionFor(int peer) => (peer % 4) switch
    {
        1 => Vector3.Right,
        2 => Vector3.Back,
        3 => Vector3.Left,
        _ => Vector3.Forward,
    };

    protected override void Gather() => Movement = Held ? Direction : Vector3.Zero;
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

    /// <summary>
    /// The longest run of consecutive ticks this peer never got the real input for, which is what a loss burst
    /// leaves behind.
    /// <para>
    /// Counted from the last answer recorded for each tick rather than from each call, because a tick is simulated
    /// again every time a correction lands: a tick that was predicted at first and then resimulated with the input
    /// that finally arrived is not a tick anyone had to guess at in the end. A count of predicted simulations cannot
    /// tell those apart, and it is the ones that never healed that matter.
    /// </para>
    /// </summary>
    /// <param name="upToTick">
    /// Ignore anything newer. The newest ticks of a node driven by a remote peer are always predicted - that peer's
    /// input for them is still a round trip away - so counting them measures the tail of the run rather than
    /// anything that went wrong, and it does so identically no matter what the netcode does.
    /// </param>
    public int LongestPredictedRun(int upToTick)
    {
        var longest = 0;
        var run = 0;
        var previous = int.MinValue;

        foreach (var tick in _predictedAt.Keys.Where(at => at <= upToTick).Order())
        {
            run = _predictedAt[tick] ? (tick == previous + 1 ? run + 1 : 1) : 0;
            longest = Math.Max(longest, run);
            previous = tick;
        }
        return longest;
    }

    private readonly Dictionary<int, bool> _predictedAt = new();

    /// <summary>A second input node owned by the host, for upstream foxssake/netfox#236. Contributes no movement.</summary>
    public HarnessInput? Events { get; private set; }

    public static HarnessPlayer Spawn(Node parent, int ownerPeer, bool enablePrediction = false, bool withServerEvents = false)
    {
        var player = new HarnessPlayer { Name = $"Player_{ownerPeer}" };
        player.SetMultiplayerAuthority(1);

        player.Input = new HarnessInput { Name = "Input", Direction = HarnessInput.DirectionFor(ownerPeer) };
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

        var predicting = Synchronizer.IsPredicting();
        _predictedAt[tick] = predicting;
        if (predicting) PredictedTicks++;
    }
}
