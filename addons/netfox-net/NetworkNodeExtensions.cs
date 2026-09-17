using Godot;

namespace Netfox;

/// <summary>
/// The everyday calls, on the game's own nodes: <c>crate.Push(impulse)</c> rather than
/// <c>GetNode&lt;NetworkObject&gt;("NetworkObject").Push(impulse)</c>. Each resolves the node's <see cref="NetworkObject"/>.
/// </summary>
public static class NetworkNodeExtensions
{
    /// <summary>The <see cref="NetworkObject"/> replicating <paramref name="node"/>: registered with it as root, or its child.</summary>
    /// <exception cref="InvalidOperationException">The node has no NetworkObject.</exception>
    public static NetworkObject Net(this Node node)
    {
        if (NetworkObject.Of(node) is { } registered) return registered;
        foreach (var child in node.GetChildren())
            if (child is NetworkObject obj && (obj.Root is null || obj.Root == node)) return obj;
        throw new InvalidOperationException($"{node.Name} has no NetworkObject child");
    }

    /// <inheritdoc cref="NetworkObject.Push(Vector3)"/>
    public static void Push(this Node target, Vector3 impulse) => target.Net().Push(impulse);

    /// <inheritdoc cref="NetworkObject.Push(NetworkObject, Vector3)"/>
    public static void Push(this Node striker, Node target, Vector3 impulse) => striker.Net().Push(target.Net(), impulse);

    /// <inheritdoc cref="NetworkObject.TryClaim"/>
    public static bool TryClaim(this Node node) => node.Net().TryClaim();

    /// <inheritdoc cref="NetworkObject.Release"/>
    public static bool Release(this Node node) => node.Net().Release();

    /// <inheritdoc cref="NetworkObject.Throw"/>
    public static bool Throw(this Node node, Vector3 velocity) => node.Net().Throw(velocity);
}
