using Godot;

namespace CrackNet.Tests;

/// <summary>The sample's scenes are what people copy: their NetworkObject has to be wired the way the docs say.</summary>
public partial class PlaygroundSceneTests : TestSuite
{
    [Test]
    public void TheCrateAndThePlayerSmoothTheirVisualOnAHandover()
    {
        foreach (var path in new[] { "res://examples/playground/PlaygroundCrate.tscn", "res://examples/playground/PlaygroundPlayer.tscn" })
        {
            var root = GD.Load<PackedScene>(path).Instantiate<Node3D>();
            var visual = root.GetNode<NetworkObject>("NetworkObject").Visual;
            Expect.True(visual is not null && visual == root.GetNodeOrNull("Visual"), $"{path}: Visual is {visual?.Name.ToString() ?? "not set"}");
            root.Free();
        }
    }

    /// <summary>
    /// A gesture plays on the player it was sent to and on no other. An AnimationTree's nodes are one resource shared
    /// by every player instanced from the scene, and only its parameters belong to the single player: a slot per clip
    /// is fired through a parameter, so one knight's throw cannot reach into another's.
    /// </summary>
    [Test]
    public async Task AGesturePlaysOnOnePlayerOnly()
    {
        var scene = GD.Load<PackedScene>("res://examples/playground/PlaygroundPlayer.tscn");
        var players = new[] { scene.Instantiate<Examples.Playground.PlaygroundPlayer>(), scene.Instantiate<Examples.Playground.PlaygroundPlayer>() };
        foreach (var player in players) AddChild(player);
        players[1].Gesture = new Vector2I(0, 1);   // a throw, on the second knight only
        for (var i = 0; i < 5; i++) await NextFrame();
        var playing = players.Select(player => player.GetNode<AnimationTree>("AnimationTree").Get("parameters/Throw/active").AsBool()).ToArray();
        foreach (var player in players) player.QueueFree();
        Expect.True(!playing[0] && playing[1], $"the throw plays on knight one: {playing[0]}, on knight two: {playing[1]}");
    }

    /// <summary>
    /// A shot or a flinch plays on the upper body: the legs keep the stride they were walking rather than standing
    /// still for the second the clip lasts. Two knights walk side by side, one of them hit; their feet have to agree
    /// and their hands must not.
    /// </summary>
    [Test]
    public async Task AFlinchLeavesTheLegsWalking()
    {
        var scene = GD.Load<PackedScene>("res://examples/playground/PlaygroundPlayer.tscn");
        var players = new[] { scene.Instantiate<Examples.Playground.PlaygroundPlayer>(), scene.Instantiate<Examples.Playground.PlaygroundPlayer>() };
        foreach (var player in players)
        {
            AddChild(player);
            player.WalkBlend = 1;
        }
        for (var i = 0; i < 10; i++) await NextFrame();
        players[1].Gesture = new Vector2I(3, 1);   // a flinch
        for (var i = 0; i < 20; i++) await NextFrame();

        var feet = players.Select(player => BoneOf(player, "foot.r")).ToArray();
        var hands = players.Select(player => BoneOf(player, "hand.r")).ToArray();
        foreach (var player in players) player.QueueFree();
        var report = $"feet {feet[0].DistanceTo(feet[1]):F3} m apart, hands {hands[0].DistanceTo(hands[1]):F3} m";
        GD.Print("FLINCH WHILE WALKING " + report);
        Expect.True(hands[0].DistanceTo(hands[1]) > 0.05f, "the flinch never moved the hit knight's arm, so this measures nothing: " + report);
        Expect.True(feet[0].DistanceTo(feet[1]) < 0.01f, "the flinch took the hit knight's legs out of the walk: " + report);

        static Vector3 BoneOf(Examples.Playground.PlaygroundPlayer player, string bone)
        {
            var skeleton = player.GetNode<Skeleton3D>("Visual/Knight/Rig/Skeleton3D");
            return skeleton.GetBoneGlobalPose(skeleton.FindBone(bone)).Origin;
        }
    }

    /// <summary>
    /// Aiming is a synced point and a <c>LookAtModifier3D</c> on the spine: the upper body turns to the point, and the
    /// hand a crate is carried in - a marker under a bone attachment below that bone - comes with it. Two knights, the
    /// second of them stepping across the first's line of sight so the first's aim locks on to it: what the first aims
    /// at is the point it decided on, not the other knight, and its hand has moved by the frame this reads it.
    /// </summary>
    [Test]
    public async Task TheUpperBodyAndTheHandTurnToTheAimedPoint()
    {
        var scene = GD.Load<PackedScene>("res://examples/playground/PlaygroundPlayer.tscn");
        var knights = new[] { scene.Instantiate<Examples.Playground.PlaygroundPlayer>(), scene.Instantiate<Examples.Playground.PlaygroundPlayer>() };
        foreach (var knight in knights) AddChild(knight);
        // In front of the first knight, which faces -Z: near enough and central enough for its aim to lock on
        knights[1].GlobalPosition = new Vector3(0, 0, -2);
        for (var i = 0; i < 20; i++) await NextFrame();
        var (ahead, aheadHand) = Aim(knights[0]);

        knights[1].GlobalPosition = new Vector3(1.2f, 0, -2);
        for (var i = 0; i < 20; i++) await NextFrame();
        var (across, acrossHand) = Aim(knights[0]);
        var aimed = knights[0].AimAt;
        var other = knights[1].GlobalPosition;
        foreach (var knight in knights) knight.QueueFree();

        var turned = Mathf.RadToDeg(Mathf.Atan2(across.X, -across.Z) - Mathf.Atan2(ahead.X, -ahead.Z));
        var report = $"the aim is {aimed} and the other knight at {other}; the body turned {turned:F0} degrees and the hand moved {aheadHand.DistanceTo(acrossHand):F2} m";
        GD.Print("AIM " + report);
        Expect.True((aimed with { Y = 0 }).DistanceTo(other with { Y = 0 }) < 0.01f, "the aim did not lock on to the knight in front, so this measures nothing: " + report);
        Expect.True(turned > 15, "the modifier did not turn the upper body to the aim: " + report);
        Expect.True(aheadHand.DistanceTo(acrossHand) > 0.2f, "the carrying hand did not come with the upper body: " + report);

        // The flat reach from the spine to the carrying hand, and where that hand is: both after the modifier ran
        static (Vector3 Reach, Vector3 Hand) Aim(Examples.Playground.PlaygroundPlayer knight)
        {
            var skeleton = knight.GetNode<Skeleton3D>("Visual/Knight/Rig/Skeleton3D");
            var spine = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(skeleton.FindBone("spine")).Origin;
            return ((knight.Hand.GlobalPosition - spine) with { Y = 0 }, knight.Hand.GlobalPosition);
        }
    }

    /// <summary>
    /// The throw starts at the swing, not at the wind-up before it: the clip spends its first half second pulling the
    /// arm back, and playing that first put half a second between the key and the crate leaving the hand, which reads
    /// as the item waiting for the animation to end. Counted in frames from the gesture to the arm being out front.
    /// </summary>
    [Test]
    public async Task AThrowSwingsWithoutPlayingItsWindUp()
    {
        var player = GD.Load<PackedScene>("res://examples/playground/PlaygroundPlayer.tscn").Instantiate<Examples.Playground.PlaygroundPlayer>();
        AddChild(player);
        for (var i = 0; i < 10; i++) await NextFrame();
        player.Gesture = new Vector2I(0, 1);   // a throw

        var skeleton = player.GetNode<Skeleton3D>("Visual/Knight/Rig/Skeleton3D");
        int hand = skeleton.FindBone("hand.r"), hips = skeleton.FindBone("hips");
        // In seconds of animation, not frames: headless the runner draws far faster than a screen does
        var seconds = 0.0;
        var reach = 0f;
        while (seconds < 0.9)
        {
            await NextFrame();
            seconds += GetProcessDeltaTime();
            reach = skeleton.GetBoneGlobalPose(hand).Origin.Z - skeleton.GetBoneGlobalPose(hips).Origin.Z;
            if (reach > 0.4f) break;
        }
        player.QueueFree();
        var report = $"the hand was out front {seconds:F2} s after the gesture, at {reach:F2} m";
        GD.Print("THROW SWING " + report);
        Expect.True(reach > 0.4f && seconds < 0.35, "the throw plays its wind-up before the swing: " + report);
    }
}
