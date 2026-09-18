// The nodes here are built in code, not from scenes: CRN006 has nothing to check
#pragma warning disable CRN006

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

    /// <summary>
    /// The inspector's [Synced] list finds a script's type by its path. A subclass of another script carries its base's
    /// path too, and reading attributes with inheritance threw on it in the editor.
    /// </summary>
    [Test]
    public void AScriptIsFoundByItsPathWhenItsTypeSubclassesAnotherScript()
    {
        Expect.Equal(typeof(NetworkNodeExtensionsTests), NetworkObject.TypeOfScript("res://test/Nodes/NetworkNodeExtensionsTests.cs"));
    }

    /// <summary>Smoothing moves Visual: pointed at the root it would move the body, outside the object someone else.</summary>
    [Test]
    public void VisualHasToBeUnderTheRootAndNotTheRoot()
    {
        var root = new RigidBody3D();
        var pivot = new Node3D();
        var model = new Node3D();
        var elsewhere = new Node3D();
        root.AddChild(pivot);
        pivot.AddChild(model);
        Expect.Null(NetworkObject.VisualProblem(root, pivot));
        Expect.Null(NetworkObject.VisualProblem(root, model));
        Expect.Null(NetworkObject.VisualProblem(root, null));
        Expect.True(NetworkObject.VisualProblem(root, root) is not null, "the root itself was accepted");
        Expect.True(NetworkObject.VisualProblem(root, elsewhere) is not null, "a node outside the object was accepted");
        root.Free();
        elsewhere.Free();
    }

    [Test]
    public void TheFirstAuthorityNotificationComesAfterTheNodeIsReady()
    {
        var watcher = new AuthorityWatcher { Name = "Watcher" };
        watcher.AddChild(new NetworkObject { Name = "NetworkObject" });
        AddChild(watcher);
        Expect.Equal(1, watcher.Notified);
        Expect.Equal(0, watcher.NotifiedBeforeReady);
    }

    [Test]
    public async Task ImpulseVelocityAddsUpAndFadesOnItsOwn()
    {
        var player = new ImpulsedWatcher { Name = "Player" };
        // Fades 3 m/s in one physics frame at 60 Hz
        player.AddChild(new NetworkObject { Name = "NetworkObject", ImpulseDecay = 180 });
        await Mount(player);

        // Offline this peer is the authority, so pushes land here at once, and the node's hook hears each
        player.Impulse(new Vector3(2, 0, 0));
        player.Impulse(new Vector3(1, 0, 0));
        Expect.Equal(new Vector3(3, 0, 0), player.ImpulseVelocity);
        Expect.SequenceEqual([new Vector3(2, 0, 0), new Vector3(1, 0, 0)], player.Heard);

        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        Expect.True(player.ImpulseVelocity.Length() < 3, $"the library did not fade it: {player.ImpulseVelocity}");
        Expect.True(await WaitUntil(() => player.ImpulseVelocity == Vector3.Zero, 1), $"never faded out: {player.ImpulseVelocity}");
    }
}
