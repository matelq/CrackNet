using Godot;

namespace Netfox;

/// <summary>
/// Implemented by a replicated node that wants to know when it changed hands: who simulates it, or who holds it. The
/// library calls <see cref="OnAuthorityChanged"/> on every peer, right after it has applied the change, so
/// <c>this.Authority</c> and <c>this.Holder</c> already read the new values.
/// <para>
/// Preferred over subscribing to <see cref="NetworkObject.AuthorityChanged"/> from the node itself: nothing to
/// unsubscribe in <c>_ExitTree</c>. The event stays for watching someone else's object.
/// </para>
/// <example>
/// <code>
/// public partial class Crate : RigidBody3D, IAuthorityChanged
/// {
///     public void OnAuthorityChanged() => _material.AlbedoColor = ColorOf(this.Authority.Peer);
/// }
/// </code>
/// </example>
/// </summary>
public interface IAuthorityChanged
{
    /// <summary>Called on every peer after the authority or the holder of this node changed.</summary>
    void OnAuthorityChanged();
}
