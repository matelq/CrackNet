using Godot;

namespace Netfox.Tests;

/// <summary>A peer joining mid-session gets the world as it is: spawned objects, who simulates and holds what, and state.</summary>
public partial class LateJoinTests : HarnessSuite
{
    private const int Shooter = 2;
    private static readonly Vector3 Speed = new(2, 0, 0);

    private static void AddSpawner(Node stack)
    {
        var spawner = new MultiplayerSpawner { Name = "ShooterSpawner", SpawnPath = new NodePath("..") };
        spawner.SpawnFunction = Callable.From((Variant data) => (Node)HarnessBody.Create(data.AsString(), Shooter, Speed));
        spawner.SetMultiplayerAuthority(Shooter);
        stack.AddChild(spawner);
    }

    [Test]
    public async Task ALateJoinerSeesWhoHoldsWhatAndWhatWasSpawned()
    {
        AddSpawner(Host);
        AddSpawner(Client);
        HarnessBody.Spawn(Host, "Crate", 1, Speed);
        var crate = HarnessBody.Spawn(Client, "Crate", 1, Speed);
        Expect.True(await WaitUntil(() => Client.Context.NetworkTime.IsInitialSyncDone(), 5), "client never synced");

        Expect.True(crate.Object.TryGrab());
        Client.GetNode<MultiplayerSpawner>("ShooterSpawner").Spawn("Bullet");
        Expect.True(await WaitUntil(() => Host.GetNodeOrNull("Bullet") is not null && ((HarnessBody)Host.GetNode("Crate")).Object.Owner == 2, 5),
            "host never saw the grab and the shot");

        var late = AddPeer(4);
        AddSpawner(late);
        // The scene object shows up on the late peer a while after it connected: the host's word has to wait for it
        for (var i = 0; i < 15; i++) await NextFrame();
        var lateCrate = HarnessBody.Spawn(late, "Crate", 1, Speed);

        var caughtUp = await WaitUntil(() =>
            lateCrate.Object.Authority == 2 && lateCrate.Object.Owner == 2
            && lateCrate.Ticks > crate.Ticks - 10 && lateCrate.Visible
            && late.GetNodeOrNull<HarnessBody>("Bullet") is { Visible: true }, 5);

        Expect.True(caughtUp,
            $"crate auth {lateCrate.Object.Authority} owner {lateCrate.Object.Owner} ticks {lateCrate.Ticks}/{crate.Ticks} visible {lateCrate.Visible}, " +
            $"bullet {late.GetNodeOrNull<HarnessBody>("Bullet")?.Visible}");
    }
}
