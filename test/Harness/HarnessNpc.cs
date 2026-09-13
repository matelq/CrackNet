using Godot;

namespace Netfox.Tests;

/// <summary>
/// A rollback root with no input at all: it moves by a rule, and only its authority runs the rule. The harness had
/// nothing of this shape - every subject was somebody's player - and the code paths for "a node nobody drives" were
/// never exercised in isolation (netfox-net#57).
/// <para>
/// netfox simulates an inputless node on <i>every</i> peer by default (RollbackSimulationServer: "node has no input,
/// simulate it"). The authority guard in <see cref="RollbackTick"/> is the pattern for an object that should instead
/// be told where it is - clients keep the restored state and never run the rule.
/// </para>
/// </summary>
public partial class HarnessNpc : Node3D, IRollbackTick
{
    public const float Speed = 2.0f;

    public RollbackSynchronizer Synchronizer { get; private set; } = null!;
    public int SimulatedTicks { get; private set; }

    /// <summary>Which way it is going; state, so a rewind restores it with the position.</summary>
    public Vector3 Heading { get; set; } = Vector3.Right;

    /// <summary>Stand still from this tick on, so the case can ask where it came to rest. Negative: never stops.</summary>
    public int StopAtTick { get; set; } = -1;

    public static HarnessNpc Spawn(Node parent)
    {
        var npc = new HarnessNpc { Name = "Npc" };
        npc.SetMultiplayerAuthority(1);
        npc.Synchronizer = new RollbackSynchronizer
        {
            Name = "RollbackSynchronizer",
            Root = npc,
            StateProperties = [":position", ":Heading"],
        };
        npc.AddChild(npc.Synchronizer);
        parent.AddChild(npc);
        return npc;
    }

    public void RollbackTick(double delta, int tick, bool isFresh)
    {
        if (!IsMultiplayerAuthority()) return;
        SimulatedTicks++;
        if (StopAtTick >= 0 && tick >= StopAtTick) return;
        // A slow circle: deterministic, never still, and a function of nothing but this tick's state
        Heading = Heading.Rotated(Vector3.Up, 0.05f).Normalized();
        Position += Heading * Speed * (float)delta;
    }
}
