using Godot;

namespace Netfox;

/// <summary>
/// The everyday calls, on the game's own nodes: <c>crate.Push(impulse)</c> and <c>this.Authority.IsLocal</c> rather
/// than <c>GetNode&lt;NetworkObject&gt;("NetworkObject")</c>. Each resolves the node's <see cref="NetworkObject"/>, so
/// game code names that class only for the rare things: <c>Net().AuthorityChanged</c>, <c>Net().Send</c>,
/// <c>Net().Diagnostics</c>.
/// </summary>
public static class NetworkNodeExtensions
{
    extension(Node node)
    {
        /// <inheritdoc cref="NetworkObject.Authority"/>
        public NetworkObject.ObjectAuthority Authority => node.Net().Authority;

        /// <inheritdoc cref="NetworkObject.Holder"/>
        public int Holder => node.Net().Holder;

        /// <inheritdoc cref="NetworkObject.Push(Vector3)"/>
        public void Push(Vector3 impulse) => node.Net().Push(impulse);

        /// <inheritdoc cref="NetworkObject.Push(NetworkObject, Vector3)"/>
        public void Push(Node target, Vector3 impulse) => node.Net().Push(target.Net(), impulse);

        /// <inheritdoc cref="NetworkObject.TryClaim"/>
        public bool TryClaim() => node.Net().TryClaim();

        /// <inheritdoc cref="NetworkObject.Release"/>
        public bool Release() => node.Net().Release();

        /// <inheritdoc cref="NetworkObject.Throw"/>
        public bool Throw(Vector3 velocity) => node.Net().Throw(velocity);

        /// <inheritdoc cref="NetworkObject.TakeKnockback"/>
        public Vector3 TakeKnockback(double delta, float decay = 20) => node.Net().TakeKnockback(delta, decay);

        /// <inheritdoc cref="NetworkObject.Despawn"/>
        public bool Despawn() => node.Net().Despawn();
    }

    /// <summary>The <see cref="NetworkObject"/> replicating <paramref name="node"/>: registered with it as root, or its child.</summary>
    /// <exception cref="InvalidOperationException">The node has no NetworkObject.</exception>
    public static NetworkObject Net(this Node node)
    {
        if (NetworkObject.Of(node) is { } registered) return registered;
        foreach (var child in node.GetChildren())
            if (child is NetworkObject obj && (obj.Root is null || obj.Root == node)) return obj;
        throw new InvalidOperationException($"{node.Name} has no NetworkObject child");
    }
}
