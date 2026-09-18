namespace CrackNet;

/// <summary>
/// Implemented by a replicated node that wants to know when something was attached to it or detached from it, or
/// when it was itself attached or detached. The library calls <see cref="OnAttachmentChanged"/> on every peer, at
/// the moment that peer shows the change: on the authority as it happens, elsewhere when playback reaches it, so
/// <see cref="NetworkObject.Attached"/> and <see cref="NetworkObject.AttachedTo"/> already read the new values.
/// <para>
/// Preferred over subscribing to <see cref="NetworkObject.AttachmentChanged"/> from the node itself: nothing to
/// unsubscribe in <c>_ExitTree</c>. The event stays for watching another object.
/// </para>
/// </summary>
public interface IAttachmentChanged
{
    /// <summary>Called on every peer after this node's attachments, or its own attachment, changed there.</summary>
    void OnAttachmentChanged();
}
