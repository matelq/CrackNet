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

    public static Walker Walker(CrackNetStack stack, int peer, Vector3 position, Vector3 velocity, float pushStrength = 0, string? name = null)
    {
        var walker = new Walker { Name = name ?? $"Walker{peer}", Position = position, Walk = velocity };
        walker.SetMultiplayerAuthority(peer);
        walker.AddChild(Shapes.Collision(new CapsuleShape3D { Radius = 0.4f, Height = 1.8f }));
        walker.AddChild(new NetworkObject { Name = "NetworkObject", ImpulseStrength = pushStrength });
        World(stack).AddChild(walker);
        return walker;
    }

    /// <summary>The playground's kind of platform: an animatable body the host moves in its physics step, played back elsewhere.</summary>
    public static Lift Lift(CrackNetStack stack, string name, Vector3 position, Vector3 size, Vector3 velocity)
    {
        var lift = new Lift { Name = name, Position = position, Velocity = velocity };
        lift.SetMultiplayerAuthority(1);
        lift.AddChild(Shapes.Collision(new BoxShape3D { Size = size }));
        lift.AddChild(new NetworkObject { Name = "NetworkObject" });
        World(stack).AddChild(lift);
        return lift;
    }

    /// <summary>A long floating slab the host drives at a set velocity: a lift or a moving crate, for riders to stand on.</summary>
    public static RigidBody3D Platform(CrackNetStack stack, string name, Vector3 position, Vector3 size)
    {
        var platform = new RigidBody3D
        {
            Name = name,
            Position = position,
            GravityScale = 0,
            LockRotation = true,
            LinearDampMode = RigidBody3D.DampMode.Replace,
            LinearDamp = 0,
            CanSleep = false,
        };
        platform.SetMultiplayerAuthority(1);
        platform.AddChild(Shapes.Collision(new BoxShape3D { Size = size }));
        platform.AddChild(new NetworkObject { Name = "NetworkObject" });
        World(stack).AddChild(platform);
        return platform;
    }

    /// <summary>
    /// A walker moved by root motion: a one-bone skeleton whose bone an AnimationPlayer strides 2 m a second along +X,
    /// extracted as root motion and applied by the walker's own controller.
    /// </summary>
    public static Walker RootMotionWalker(CrackNetStack stack, int peer, string name, Vector3 position)
    {
        var walker = Walker(stack, peer, position, Vector3.Zero, name: name);
        var skeleton = new Skeleton3D { Name = "Skeleton" };
        skeleton.AddBone("hips");
        skeleton.SetBoneRest(0, Transform3D.Identity);
        walker.AddChild(skeleton);

        var animation = new Animation { Length = 1, LoopMode = Animation.LoopModeEnum.Linear };
        var track = animation.AddTrack(Animation.TrackType.Position3D);
        animation.TrackSetPath(track, new NodePath("Skeleton:hips"));
        animation.PositionTrackInsertKey(track, 0, Vector3.Zero);
        animation.PositionTrackInsertKey(track, 1, new Vector3(2, 0, 0));
        var library = new AnimationLibrary();
        library.AddAnimation("stride", animation);
        var player = new AnimationPlayer
        {
            Name = "Animation",
            RootMotionTrack = new NodePath("Skeleton:hips"),
            CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Physics,
        };
        player.AddAnimationLibrary("", library);
        walker.AddChild(player);
        player.Play("stride");
        walker.RootMotion = player;
        return walker;
    }

    /// <summary>
    /// A hand at the end of a bone that a LookAtModifier3D aims at a target circling the carrier: IK moves the hand
    /// in the skeleton's own pass, after the animation.
    /// </summary>
    public static Marker3D AimingHand(Node3D carrier)
    {
        var skeleton = new Skeleton3D { Name = "Skeleton" };
        skeleton.AddBone("arm");
        var rest = new Transform3D(Basis.Identity, new Vector3(0, 0.8f, 0));
        skeleton.SetBoneRest(0, rest);
        skeleton.SetBonePosePosition(0, rest.Origin);
        carrier.AddChild(skeleton);
        var attachment = new BoneAttachment3D { Name = "ArmBone", BoneName = "arm" };
        skeleton.AddChild(attachment);
        var hand = new Marker3D { Name = "Hand", Position = new Vector3(0, 0, -1) };
        attachment.AddChild(hand);

        var target = new Marker3D { Name = "Target", Position = new Vector3(1.5f, 0.8f, 0) };
        carrier.AddChild(target);
        var animation = new Animation { Length = 2, LoopMode = Animation.LoopModeEnum.Linear };
        var track = animation.AddTrack(Animation.TrackType.Position3D);
        animation.TrackSetPath(track, new NodePath("Target"));
        animation.PositionTrackInsertKey(track, 0, new Vector3(1.5f, 0.8f, 0));
        animation.PositionTrackInsertKey(track, 0.5, new Vector3(0, 0.8f, -1.5f));
        animation.PositionTrackInsertKey(track, 1, new Vector3(-1.5f, 0.8f, 0));
        animation.PositionTrackInsertKey(track, 1.5, new Vector3(0, 0.8f, 1.5f));
        animation.PositionTrackInsertKey(track, 2, new Vector3(1.5f, 0.8f, 0));
        var library = new AnimationLibrary();
        library.AddAnimation("circle", animation);
        var player = new AnimationPlayer { Name = "Animation" };
        player.AddAnimationLibrary("", library);
        carrier.AddChild(player);
        player.Play("circle");

        var lookAt = new LookAtModifier3D { Name = "Aim", BoneName = "arm", ForwardAxis = SkeletonModifier3D.BoneAxis.MinusZ };
        skeleton.AddChild(lookAt);
        lookAt.TargetNode = lookAt.GetPathTo(target);
        return hand;
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

    /// <summary>
    /// A one-bone skeleton under <paramref name="carrier"/> whose bone an AnimationPlayer swings 0.6 m up and down twice a
    /// second, and a hand marker under a <see cref="BoneAttachment3D"/> on that bone: the skeleton applies its poses and
    /// moves the attachment in a deferred notification, after every node's process, which is where an item placed in
    /// an ordinary <c>_Process</c> would read the bone a frame late.
    /// </summary>
    public static Marker3D BoneHand(Node3D carrier)
    {
        var skeleton = new Skeleton3D { Name = "Skeleton" };
        skeleton.AddBone("hand");
        var rest = new Transform3D(Basis.Identity, new Vector3(0, 0.8f, -1));
        skeleton.SetBoneRest(0, rest);
        skeleton.SetBonePosePosition(0, rest.Origin);
        carrier.AddChild(skeleton);
        var attachment = new BoneAttachment3D { Name = "HandBone", BoneName = "hand" };
        skeleton.AddChild(attachment);
        var hand = new Marker3D { Name = "Hand" };
        attachment.AddChild(hand);

        var animation = new Animation { Length = 0.5f, LoopMode = Animation.LoopModeEnum.Linear };
        var track = animation.AddTrack(Animation.TrackType.Position3D);
        animation.TrackSetPath(track, new NodePath("Skeleton:hand"));
        animation.PositionTrackInsertKey(track, 0, rest.Origin);
        animation.PositionTrackInsertKey(track, 0.25, rest.Origin + Vector3.Up * 0.6f);
        animation.PositionTrackInsertKey(track, 0.5, rest.Origin);
        var library = new AnimationLibrary();
        library.AddAnimation("swing", animation);
        var player = new AnimationPlayer { Name = "Animation" };
        player.AddAnimationLibrary("", library);
        carrier.AddChild(player);
        player.Play("swing");
        return hand;
    }
}
