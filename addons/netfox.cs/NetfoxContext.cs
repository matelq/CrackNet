using Godot;

namespace Netfox;

/// <summary>
/// The netfox servers as one owned graph instead of process-wide singletons.
/// <para>
/// Upstream has no equivalent: its servers are GDScript autoloads, so a process can only ever run one netfox stack.
/// The autoloads still exist here and still fill <c>Instance</c>, but they register into <see cref="Default"/>, and a
/// second stack can be created by adding a <see cref="NetfoxContextRoot"/> to the tree: everything below it resolves to
/// that context instead, which is what an in-process two-peer test needs.
/// </para>
/// </summary>
public sealed class NetfoxContext
{
    /// <summary>The context the autoloads register into, and the one nodes outside a <see cref="NetfoxContextRoot"/> use.</summary>
    public static NetfoxContext Default { get; } = new();

    public NetworkCommandServer NetworkCommandServer { get; internal set; } = null!;
    public NetworkTime NetworkTime { get; internal set; } = null!;
    public NetworkTimeSynchronizer NetworkTimeSynchronizer { get; internal set; } = null!;
    public NetworkEvents NetworkEvents { get; internal set; } = null!;
    public NetworkIdentityServer NetworkIdentityServer { get; internal set; } = null!;
    public RollbackLivenessServer RollbackLivenessServer { get; internal set; } = null!;
    public NetworkHistoryServer NetworkHistoryServer { get; internal set; } = null!;
    public RollbackSimulationServer RollbackSimulationServer { get; internal set; } = null!;
    public NetworkSynchronizationServer NetworkSynchronizationServer { get; internal set; } = null!;
    public NetworkRollback NetworkRollback { get; internal set; } = null!;
    public NetworkPerformance NetworkPerformance { get; internal set; } = null!;
    public InterpolationServer InterpolationServer { get; internal set; } = null!;

    /// <summary>True for the context the autoloads live in; only its servers are published as <c>Instance</c>.</summary>
    public bool IsDefault => ReferenceEquals(this, Default);

    /// <summary>
    /// The context <paramref name="node"/> belongs to: the nearest <see cref="NetfoxContextRoot"/> above it, or
    /// <see cref="Default"/>. Nodes resolve this once, when they enter the tree.
    /// </summary>
    public static NetfoxContext For(Node node)
    {
        for (Node? current = node; current is not null; current = current.GetParent())
            if (current is NetfoxContextRoot root)
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
            new RollbackLivenessServer(),
            new NetworkHistoryServer(),
            new RollbackSimulationServer(),
            new NetworkSynchronizationServer(),
            new NetworkRollback(),
            new NetworkPerformance(),
            new InterpolationServer(),
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
public partial class NetfoxContextRoot : Node
{
    /// <summary>The context this subtree uses. Assign before entering the tree to share one between roots.</summary>
    public NetfoxContext Context { get; set; } = new();

    public override void _EnterTree() => Context.CreateServers(this);
}
