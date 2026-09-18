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
}
