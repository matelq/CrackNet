using Godot;

namespace CrackNet;

/// <summary>
/// The everyday calls, on the game's own nodes: <c>crate.Impulse(impulse)</c> and <c>this.Authority.IsLocal</c> rather
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

        /// <inheritdoc cref="NetworkObject.ClaimedBy"/>
        public int ClaimedBy => node.Net().ClaimedBy;

        /// <inheritdoc cref="NetworkObject.Impulse(Vector3)"/>
        public void Impulse(Vector3 impulse) => node.Net().Impulse(impulse);

        /// <inheritdoc cref="NetworkObject.Impulse(NetworkObject, Vector3)"/>
        public void Impulse(Node target, Vector3 impulse) => node.Net().Impulse(target.Net(), impulse);

        /// <inheritdoc cref="NetworkObject.TryClaim"/>
        public bool TryClaim() => node.Net().TryClaim();

        /// <inheritdoc cref="NetworkObject.ReleaseClaim()"/>
        public bool ReleaseClaim() => node.Net().ReleaseClaim();

        /// <inheritdoc cref="NetworkObject.ReleaseClaim(Vector3)"/>
        public bool ReleaseClaim(Vector3 velocity) => node.Net().ReleaseClaim(velocity);

        /// <inheritdoc cref="NetworkObject.TakeImpulses"/>
        public Vector3 TakeImpulses(double delta, float decay = 20) => node.Net().TakeImpulses(delta, decay);

        /// <inheritdoc cref="NetworkObject.PlaybackState"/>
        public PlaybackState PlaybackState => node.Net().PlaybackState;

        /// <inheritdoc cref="NetworkObject.Snap"/>
        public void Snap() => node.Net().Snap();

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
