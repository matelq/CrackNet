using Godot;

namespace Netfox.Tests;

/// <summary>A generated Spawn puts a scene on every peer, and freeing it or its peer leaving takes it off everywhere.</summary>
public partial class SpawnTests : HarnessSuite
{
    private NetfoxStack _third = null!;

    public override async Task BeforeCase()
    {
        await base.BeforeCase();
        _third = AddPeer(3);
        var synced = await WaitUntil(() => Client.Context.NetworkTime.IsInitialSyncDone() && _third.Context.NetworkTime.IsInitialSyncDone(), 5);
        Expect.True(synced, "peers never synced");
    }

    private NetfoxStack[] Stacks => [Host, Client, _third];

    [Test]
    public async Task ASpawnAppearsEverywhereAtItsGlobalTransformWithItsAuthorityAndData()
    {
        // A parent away from the origin: the spawn transform is global, not relative to it
        foreach (var stack in Stacks) stack.AddChild(new Node3D { Name = "Offset", Position = new Vector3(10, 0, 0) });
        var at = new Transform3D(Basis.Identity, new Vector3(3, 1, 2));
        var velocity = new Vector3(0, 0, 5);

        // The host spawns a body for peer 3, as it does a player's character
        var name = HarnessBody.Spawn(at, velocity, parent: Host.GetNode("Offset"), authority: 3).Name.ToString();

        Expect.True(await WaitUntil(() => Stacks.All(stack => stack.GetNodeOrNull<HarnessBody>($"Offset/{name}") is not null), 3),
            $"not spawned everywhere: {string.Join(", ", Stacks.Select(stack => stack.GetNodeOrNull($"Offset/{name}") is not null))}");
        foreach (var stack in Stacks)
        {
            var body = stack.GetNode<HarnessBody>($"Offset/{name}");
            Expect.Equal(3, body.GetMultiplayerAuthority(), $"{stack.Name}: authority");
            Expect.Equal(velocity, body.Velocity, $"{stack.Name}: spawn data");
            Expect.True(body.GlobalPosition.DistanceTo(new Vector3(3, 1, 2)) < 0.01f || body.Object.Authority.IsLocal,
                $"{stack.Name} placed it at {body.GlobalPosition}");
        }
        // The peer that simulates it got the data too, and moves it with that velocity
        var onThird = _third.GetNode<HarnessBody>($"Offset/{name}");
        Expect.True(onThird.Object.Authority.IsLocal, "peer 3 does not simulate it");
    }

    [Test]
    public async Task FreeingASpawnOnItsAuthorityFreesItEverywhere()
    {
        var spawned = HarnessBody.Spawn(Transform3D.Identity, parent: Client);
        var name = spawned.Name.ToString();
        Expect.True(await WaitUntil(() => Stacks.All(stack => stack.GetNodeOrNull(name) is not null), 3), "not spawned everywhere");

        spawned.QueueFree();
        Expect.True(await WaitUntil(() => Stacks.All(stack => stack.GetNodeOrNull(name) is null), 3),
            $"still there: {string.Join(", ", Stacks.Select(stack => stack.GetNodeOrNull(name) is not null))}");
    }

    [Test]
    public async Task APeersPersonalSpawnsLeaveWithIt()
    {
        var personal = HarnessBody.Spawn(Transform3D.Identity, parent: _third);
        var name = personal.Name.ToString();
        Expect.True(await WaitUntil(() => Stacks.All(stack => stack.GetNodeOrNull(name) is not null), 3), "not spawned everywhere");
        // Nobody else may take it, the way a projectile or a player's character is
        personal.Object.Transferable = false;
        Expect.True(await WaitUntil(() => Stacks.All(stack => !stack.GetNode<HarnessBody>(name).Object.Transferable), 3),
            "the lock never reached everyone");

        _third.Disconnect();
        Expect.True(await WaitUntil(() => Host.GetNodeOrNull(name) is null && Client.GetNodeOrNull(name) is null, 3),
            "a departed peer's projectile stayed behind");
    }
}
