using Godot;

namespace CrackNet.Tests;

/// <summary>The calls on a game's own nodes find the node's NetworkObject, and knockback builds up and decays.</summary>
public partial class NetworkNodeExtensionsTests : TestSuite
{
    public override Task AfterCase()
    {
        FreeChildren();
        return Task.CompletedTask;
    }

    [Test]
    public void NetFindsTheChildBeforeItRegistersAndSaysSoWhenThereIsNone()
    {
        var player = new CharacterBody3D { Name = "Player" };
        var obj = new NetworkObject { Name = "NetworkObject" };
        player.AddChild(obj);
        Expect.True(ReferenceEquals(obj, player.Net()), "not in the tree yet, so not registered: found as a child");

        var bare = new Node3D { Name = "Bare" };
        try
        {
            bare.Net();
            Expect.True(false, "a node without a NetworkObject resolved");
        }
        catch (InvalidOperationException e)
        {
            Expect.True(e.Message.Contains("Bare"), e.Message);
        }
        player.Free();
        bare.Free();
    }

    [Test]
    public async Task KnockbackAddsUpAndDecays()
    {
        var player = new CharacterBody3D { Name = "Player" };
        player.AddChild(new NetworkObject { Name = "NetworkObject" });
        await Mount(player);

        // Offline this peer is the authority, so pushes land here at once
        player.Impulse(new Vector3(2, 0, 0));
        player.Impulse(new Vector3(1, 0, 0));
        Expect.Equal(new Vector3(3, 0, 0), player.Net().TakeImpulses(0.1, decay: 20));
        Expect.Equal(new Vector3(1, 0, 0), player.Net().TakeImpulses(0.1, decay: 20));
        Expect.Equal(Vector3.Zero, player.Net().TakeImpulses(0.1, decay: 20));
    }
}
