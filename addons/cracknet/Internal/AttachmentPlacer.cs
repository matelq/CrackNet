using Godot;

namespace CrackNet.Internal;

/// <summary>
/// Puts every attached item on its anchor once per rendered frame, on every peer, after that peer's animation has
/// moved the anchor. It processes last (the highest priority) and still defers the placement: a skeleton applies its
/// poses and moves its bone attachments in a deferred notification queued while it processed, so a placement queued
/// after it runs after them, whatever the order of the nodes in the tree.
/// </summary>
internal partial class AttachmentPlacer : Node
{
    internal NetworkObjectServer Server { get; init; } = null!;

    public AttachmentPlacer()
    {
        Name = "Attachments";
        ProcessPriority = int.MaxValue;
    }

    public override void _Process(double delta) => Callable.From(Server.PlaceAttached).CallDeferred();
}
