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
    /// The player's one-shot slot points at whichever clip the gesture names, and the scene's blend tree is shared by
    /// every player instanced from it: without a copy of its own, one knight's throw retargets everyone's slot.
    /// </summary>
    [Test]
    public void EachPlayersOneShotSlotPlaysItsOwnClip()
    {
        var scene = GD.Load<PackedScene>("res://examples/playground/PlaygroundPlayer.tscn");
        var players = new[] { scene.Instantiate<Examples.Playground.PlaygroundPlayer>(), scene.Instantiate<Examples.Playground.PlaygroundPlayer>() };
        foreach (var player in players) AddChild(player);
        players[0].Gesture = new Vector2I(0, 1);   // a throw
        players[1].Gesture = new Vector2I(1, 1);   // a pick-up
        var clips = players.Select(ClipOf).ToArray();
        foreach (var player in players) player.QueueFree();
        Expect.True(clips[0] == "Throw" && clips[1] == "PickUp", $"the two players' one-shot slots hold {clips[0]} and {clips[1]}: they share one blend tree");

        static string ClipOf(Examples.Playground.PlaygroundPlayer player)
        {
            var tree = (AnimationNodeBlendTree)player.GetNode<AnimationTree>("AnimationTree").TreeRoot;
            return ((AnimationNodeAnimation)tree.GetNode("GestureAnimation")).Animation;
        }
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
