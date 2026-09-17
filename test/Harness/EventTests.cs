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
            body.Object.Received += (origin, payload) => received.Add((peer, origin, payload.AsString()));
        }
        return (bodies, received);
    }

    [Test]
    public async Task APushReachesThePlayersOwnPeerOnce()
    {
        var (player, received) = SpawnEverywhere("Player", authority: 2);
        foreach (var body in player) body.Object.Transferable = false;
        await NextFrame();

        player[0].Object.Send("push from host");
        player[2].Object.Send("push from 3");
        player[1].Object.Send("own jump");

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
        crate[2].Object.Send("hit");
        Expect.True(crate[1].Object.Authority.Take());

        await WaitUntil(() => received.Count >= 1, 3);
        for (var i = 0; i < 20; i++) await NextFrame();

        Expect.SequenceEqual([(2, 3, "hit")], received);
    }

    [Test]
    public async Task AnEventOnAClaimTheHostRejectsReachesTheWinner()
    {
        // Both grab before hearing of the other; the client's request reaches the host first
        Network.SetLink(1, 2, latencyMs: 10);
        Network.SetLink(1, 3, latencyMs: 60);
        Network.SetLink(2, 3, latencyMs: 60);
        var (crate, received) = SpawnEverywhere("Crate", authority: 1);
        await NextFrame();

        Expect.True(crate[1].Object.TryGrab());
        Expect.True(crate[2].Object.TryGrab());
        // Peer 3 thinks it simulates the crate and hits it; the host is about to say it does not
        crate[2].Object.Send("hit");

        await WaitUntil(() => received.Count >= 1, 3);
        for (var i = 0; i < 20; i++) await NextFrame();

        Expect.SequenceEqual([(2, 3, "hit")], received);
    }
}
