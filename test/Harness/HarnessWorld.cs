// The harness builds its bodies in code, to vary mass, shapes and push strength per case rather than keep a scene for
// each: CRN006, which asks for a scene with a NetworkObject, has nothing to check here
#pragma warning disable CRN006

using Godot;

namespace CrackNet.Tests;

/// <summary>
/// The bodies harness cases put in a stack's world: a crate, a walker, an animated hand. Each stack gets its own
/// physics world, or the host's crate and the client's copy of it would collide with each other.
/// </summary>
internal static class HarnessWorld
{
    public static Node World(CrackNetStack stack)
    {
        if (stack.GetNodeOrNull("World") is { } world) return world;
        var viewport = new SubViewport { Name = "World", OwnWorld3D = true, Size = new Vector2I(2, 2) };
        stack.AddChild(viewport);
        var floor = new StaticBody3D { Name = "Floor", Position = new Vector3(0, -0.5f, 0) };
        floor.AddChild(Shapes.Collision(new BoxShape3D { Size = new Vector3(100, 1, 100) }));
        viewport.AddChild(floor);
        return viewport;
    }

    public static RigidBody3D Crate(CrackNetStack stack, string name, Vector3 position, bool smoothed = false)
    {
        var crate = new RigidBody3D { Name = name, Position = position };
        crate.SetMultiplayerAuthority(1);
        crate.AddChild(Shapes.Collision(new BoxShape3D { Size = Vector3.One }));
        var visual = new Node3D { Name = "Visual" };
        crate.AddChild(visual);
        // Longer than the default 0.15 s: a harness frame with four stacks is 30-110 ms, and a fade that fits in one
        // frame draws as a jump of its own. The mechanism is what is checked here, not the tuning
        crate.AddChild(new NetworkObject { Name = "NetworkObject", Visual = smoothed ? visual : null, SmoothingTime = 0.5f });
        World(stack).AddChild(crate);
        return crate;
    }

    public static Walker Walker(CrackNetStack stack, int peer, Vector3 position, Vector3 velocity, float pushStrength = 0)
    {
        var walker = new Walker { Name = $"Walker{peer}", Position = position, Walk = velocity };
        walker.SetMultiplayerAuthority(peer);
        walker.AddChild(Shapes.Collision(new CapsuleShape3D { Radius = 0.4f, Height = 1.8f }));
        walker.AddChild(new NetworkObject { Name = "NetworkObject", ImpulseStrength = pushStrength });
        World(stack).AddChild(walker);
        return walker;
    }

    /// <summary>
    /// A hand in front of <paramref name="carrier"/> that an AnimationPlayer bobs 0.6 m up and down twice a second, on
    /// every peer in its own real time, as a game's animation does. At a playback delay of 70 ms an item played back
    /// from its own samples is about 0.25 m from it.
    /// </summary>
    public static Marker3D BobbingHand(Node3D carrier)
    {
        var rest = new Vector3(0, 0.8f, -1);
        var hand = new Marker3D { Name = "Hand", Position = rest };
        carrier.AddChild(hand);

        var animation = new Animation { Length = 0.5f, LoopMode = Animation.LoopModeEnum.Linear };
        var track = animation.AddTrack(Animation.TrackType.Position3D);
        animation.TrackSetPath(track, new NodePath("Hand"));
        animation.PositionTrackInsertKey(track, 0, rest);
        animation.PositionTrackInsertKey(track, 0.25, rest + Vector3.Up * 0.6f);
        animation.PositionTrackInsertKey(track, 0.5, rest);
        var library = new AnimationLibrary();
        library.AddAnimation("bob", animation);
        var player = new AnimationPlayer { Name = "Animation" };
        player.AddAnimationLibrary("", library);
        carrier.AddChild(player);
        player.Play("bob");
        return hand;
    }
}
