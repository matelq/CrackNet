using Godot;

namespace CrackNet;

/// <summary>
/// The CrackNet servers as one owned graph instead of process-wide singletons.
/// <para>
/// Upstream has no equivalent: its servers are GDScript autoloads, so a process can only ever run one CrackNet stack.
/// The autoloads still exist here and still fill <c>Instance</c>, but they register into <see cref="Default"/>, and a
/// second stack can be created by adding a <see cref="CrackNetContextRoot"/> to the tree: everything below it resolves to
/// that context instead, which is what an in-process two-peer test needs.
/// </para>
/// </summary>
public sealed class CrackNetContext
{
    /// <summary>The context the autoloads register into, and the one nodes outside a <see cref="CrackNetContextRoot"/> use.</summary>
    public static CrackNetContext Default { get; } = new();

    public NetworkCommandServer NetworkCommandServer { get; internal set; } = null!;
    public NetworkTime NetworkTime { get; internal set; } = null!;
    public NetworkTimeSynchronizer NetworkTimeSynchronizer { get; internal set; } = null!;
    public NetworkEvents NetworkEvents { get; internal set; } = null!;
    public NetworkIdentityServer NetworkIdentityServer { get; internal set; } = null!;
    public NetworkObjectServer NetworkObjectServer { get; internal set; } = null!;

    /// <summary>True for the context the autoloads live in; only its servers are published as <c>Instance</c>.</summary>
    public bool IsDefault => ReferenceEquals(this, Default);

    /// <summary>
    /// Raised at the end of <see cref="ResetSession"/>, once the servers have dropped their per-session data. Nodes that
    /// hold ticks of their own, like the synchronizers, listen to this to re-register themselves.
    /// </summary>
    public event Action? SessionReset;

    /// <summary>
    /// Drops everything tied to the session that just ended: recorded history, what was sent to which peer, the ids
    /// exchanged with peers, spawn ticks. Registrations survive, so a scene that stays in the tree keeps working.
    /// <para>
    /// Called automatically when <see cref="NetworkEvents"/> sees the session stop. Games that disable NetworkEvents
    /// have to call it themselves; without it, the next session starts at tick zero while the histories still hold the
    /// previous session's ticks, and every write lands outside their window and is dropped.
    /// </para>
    /// </summary>
    public void ResetSession()
    {
        NetworkObjectServer?.ResetSession();
        NetworkIdentityServer?.ResetSession();

        SessionReset?.Invoke();
    }

    /// <summary>
    /// The context <paramref name="node"/> belongs to: the nearest <see cref="CrackNetContextRoot"/> above it, or
    /// <see cref="Default"/>. Nodes resolve this once, when they enter the tree.
    /// </summary>
    public static CrackNetContext For(Node node)
    {
        for (Node? current = node; current is not null; current = current.GetParent())
            if (current is CrackNetContextRoot root)
                return root.Context;

        return Default;
    }

    private bool _serversCreated;

    /// <summary>
    /// Creates the servers this context is missing as children of <paramref name="parent"/>, in the same order the
    /// plugin registers the autoloads in (dependencies first). Does nothing for the default context, whose servers are
    /// the autoloads themselves.
    /// </summary>
    public void CreateServers(Node parent)
    {
        if (IsDefault || _serversCreated) return;
        _serversCreated = true;

        foreach (var server in new Node[]
        {
            new NetworkCommandServer(),
            new NetworkTime(),
            new NetworkTimeSynchronizer(),
            new NetworkEvents(),
            new NetworkIdentityServer(),
            new NetworkObjectServer(),
        })
        {
            server.Name = server.GetType().Name;
            parent.AddChild(server);
        }
    }
}

/// <summary>
/// Marks its subtree as belonging to <see cref="Context"/>: nodes below it use that context's servers instead of the
/// autoloads. Creates the servers as its own children when it enters the tree.
/// </summary>
public partial class CrackNetContextRoot : Node
{
    /// <summary>The context this subtree uses. Assign before entering the tree to share one between roots.</summary>
    public CrackNetContext Context { get; set; } = new();

    public override void _EnterTree() => Context.CreateServers(this);
}
