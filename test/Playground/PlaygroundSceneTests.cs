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
}
