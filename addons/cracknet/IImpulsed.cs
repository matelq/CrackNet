using Godot;

namespace CrackNet;

/// <summary>
/// Implemented by a replicated node that wants to know the moment a push reaches it: a hit animation, a sound, a
/// knockback curve of its own. The library calls <c>OnImpulsed</c> on the peer simulating the node, once per
/// push, after the push has been added to <see cref="NetworkObject.ImpulseVelocity"/>. A rigid body takes the impulse
/// itself as well.
/// <para>
/// Preferred over subscribing to <see cref="NetworkObject.Impulsed"/> from the node itself: nothing to unsubscribe in
/// <c>_ExitTree</c>. The event stays for watching someone else's object.
/// </para>
/// <example>
/// <code>
/// public partial class Player : CharacterBody3D, IImpulsed
/// {
///     public void OnImpulsed(Vector3 impulse) => _animation.Play("hit");
/// }
/// </code>
/// </example>
/// </summary>
public interface IImpulsed
{
    /// <summary>Called on the authority of this node for every push delivered to it.</summary>
    void OnImpulsed(Vector3 impulse);
}
