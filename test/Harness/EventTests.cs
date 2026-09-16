using Godot;

namespace Netfox.Tests;

/// <summary>Events reach the authority of their object exactly once, wherever it moved in the meantime.</summary>
public partial class EventTests : HarnessSuite
{
    private NetfoxStack _third = null!;

    public override async Task BeforeCase()
    {
        await base.BeforeCase();
        _third = AddPeer(3);
        var synced = await WaitUntil(() => Client.Context.NetworkTime.IsInitialSyncDone() && _third.Context.NetworkTime.IsInitialSyncDone(), 5);
        Expect.True(synced, "peers never synced");
    }

    private (HarnessBody[] Bodies, List<(int Peer, int Origin, string Payload)> Received) SpawnEverywhere(string name, int authority)
    {
        HarnessBody[] bodies =
        [
            HarnessBody.Spawn(Host, name, authority),
            HarnessBody.Spawn(Client, name, authority),
            HarnessBody.Spawn(_third, name, authority),
        ];
        var received = new List<(int, int, string)>();
        foreach (var body in bodies)
        {
            var peer = body.Multiplayer.GetUniqueId();
            body.Object.EventReceived += (origin, payload) => received.Add((peer, origin, payload.AsString()));
        }
        return (bodies, received);
    }

    [Test]
    public async Task APushReachesThePlayersOwnPeerOnce()
    {
        var (player, received) = SpawnEverywhere("Player", authority: 2);
        foreach (var body in player) body.Object.Transferable = false;
        await NextFrame();

        player[0].Object.SendToAuthority("push from host");
        player[2].Object.SendToAuthority("push from 3");
        player[1].Object.SendToAuthority("own jump");

        await WaitUntil(() => received.Count >= 3, 3);
        for (var i = 0; i < 20; i++) await NextFrame();

        Expect.SequenceEqual(
            [(2, 1, "push from host"), (2, 2, "own jump"), (2, 3, "push from 3")],
            received.OrderBy(entry => entry.Origin));
    }

    [Test]
    public async Task AnEventFollowsTheAuthorityThatMovedWhileItWasOnItsWay()
    {
        Network.SetLink(1, 2, latencyMs: 10);
        Network.SetLink(1, 3, latencyMs: 80);
        Network.SetLink(2, 3, latencyMs: 80);
        var (crate, received) = SpawnEverywhere("Crate", authority: 1);
        await NextFrame();

        // Peer 3 hits the crate it believes the host simulates; the client takes it before the hit arrives
        crate[2].Object.SendToAuthority("hit");
        Expect.True(crate[1].Object.TryTakeAuthority());

        await WaitUntil(() => received.Count >= 1, 3);
        for (var i = 0; i < 20; i++) await NextFrame();

        Expect.SequenceEqual([(2, 3, "hit")], received);
    }
}
