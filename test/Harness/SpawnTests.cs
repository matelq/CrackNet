using Godot;

namespace Netfox.Tests;

/// <summary>NetworkObject.Spawn puts a scene on every peer, and freeing it or its peer leaving takes it off everywhere.</summary>
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
    public async Task ASpawnAppearsEverywhereWithItsTransformAndAuthority()
    {
        var spawned = NetworkObject.Spawn<HarnessBody>(Client, HarnessBody.Scene, body => body.Position = new Vector3(3, 1, 2));
        var name = spawned.Name.ToString();

        Expect.True(await WaitUntil(() => Stacks.All(stack => stack.GetNodeOrNull<HarnessBody>(name) is not null), 3),
            $"not spawned everywhere: {string.Join(", ", Stacks.Select(stack => stack.GetNodeOrNull(name) is not null))}");
        foreach (var stack in Stacks)
        {
            var body = stack.GetNode<HarnessBody>(name);
            Expect.Equal(2, body.GetMultiplayerAuthority());
            Expect.True(body.Position.DistanceTo(new Vector3(3, 1, 2)) < 0.01f, $"{stack.Name} placed it at {body.Position}");
        }

        // The host spawns on behalf of a player, as it does the player's character
        var forThird = NetworkObject.Spawn<HarnessBody>(Host, HarnessBody.Scene, authority: 3).Name.ToString();
        Expect.True(await WaitUntil(() => _third.GetNodeOrNull<HarnessBody>(forThird) is { } body && body.Object.IsAuthority, 3),
            "the peer the host spawned for does not simulate it");
    }

    [Test]
    public async Task FreeingASpawnOnItsAuthorityFreesItEverywhere()
    {
        var spawned = NetworkObject.Spawn<HarnessBody>(Client, HarnessBody.Scene);
        var name = spawned.Name.ToString();
        Expect.True(await WaitUntil(() => Stacks.All(stack => stack.GetNodeOrNull(name) is not null), 3), "not spawned everywhere");

        spawned.QueueFree();
        Expect.True(await WaitUntil(() => Stacks.All(stack => stack.GetNodeOrNull(name) is null), 3),
            $"still there: {string.Join(", ", Stacks.Select(stack => stack.GetNodeOrNull(name) is not null))}");
    }

    [Test]
    public async Task APeersPersonalSpawnsLeaveWithIt()
    {
        var personal = NetworkObject.Spawn<HarnessBody>(_third, HarnessBody.Scene);
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
