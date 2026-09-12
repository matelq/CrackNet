using Godot;

namespace Netfox.Examples.Playground;

/// <summary>
/// Shots fired in this session, replicated by a <c>StateSynchronizer</c> rather than a <c>RollbackSynchronizer</c>.
/// <para>
/// The difference is the point. Rollback state is recorded per tick, rewound and resimulated, because the simulation
/// has to be able to reach a different answer for a tick it already ran. A score does not: the host decides it, it
/// only ever goes up, and no rewind should ever take a shot back. Replicating it without rollback costs a fraction of
/// the machinery and cannot be corrected into something surprising.
/// </para>
/// <para>
/// The rule of thumb: if resimulating a tick could legitimately produce a different value, it is rollback state.
/// Otherwise a StateSynchronizer is enough.
/// </para>
/// </summary>
[GlobalClass]
public partial class Scoreboard : Node
{
    /// <summary>Gathered by the StateSynchronizer beside it, the same way rollback properties are.</summary>
    [SynchronizedState] public int Shots { get; set; }

    /// <summary>Counts a shot. Only meaningful on the host, which owns this node.</summary>
    public void CountShot()
    {
        if (!IsMultiplayerAuthority()) return;
        Shots++;
    }
}
