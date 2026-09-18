using Godot;

namespace Netfox.Tests;

/// <summary>Authority and ownership move between peers, and every peer ends up agreeing who has them.</summary>
public partial class AuthorityTests : HarnessSuite
{
    private NetfoxStack _third = null!;

    public override async Task BeforeCase()
    {
        await base.BeforeCase();
        _third = AddPeer(3);
        // An authority request sent before the host knows a peer is lost; the library refuses to send one offline, but
        // cannot tell that the host has not heard of it yet
        var synced = await WaitUntil(() => Client.Context.NetworkTime.IsInitialSyncDone() && _third.Context.NetworkTime.IsInitialSyncDone(), 5);
        Expect.True(synced, "peers never synced");
    }

    private HarnessBody[] SpawnEverywhere(string name, Vector3 velocity = default, Vector3 location = default, int authority = 1)
        => [
            HarnessBody.Place(Host, name, authority, velocity, location),
            HarnessBody.Place(Client, name, authority, velocity, location),
            HarnessBody.Place(_third, name, authority, velocity, location),
        ];

    private static bool Agree(HarnessBody[] bodies, int authority, int owner)
        => bodies.All(body => body.Object.Authority.Peer == authority && body.Object.ClaimedBy == owner);

    private static string Describe(HarnessBody[] bodies)
        => string.Join(", ", bodies.Select(body => $"{body.Multiplayer.GetUniqueId()}: auth {body.Object.Authority.Peer} owner {body.Object.ClaimedBy}"));

    [Test]
    public async Task TwoPeersGrabAtOnceAndExactlyOneEndsUpHoldingIt()
    {
        // Both grab before hearing of the other; the client's request reaches the host first
        Network.SetLink(1, 2, latencyMs: 10);
        Network.SetLink(1, 3, latencyMs: 60);
        Network.SetLink(2, 3, latencyMs: 60);
        var crate = SpawnEverywhere("Crate");
        await NextFrame();

        Expect.True(crate[1].Object.TryClaim());
        Expect.True(crate[2].Object.TryClaim());

        // First to the host holds it; the later grab does not take it out of the holder's hands
        Expect.True(await WaitUntil(() => Agree(crate, 2, 2), 3), Describe(crate));
        for (var i = 0; i < 20; i++) await NextFrame();
        Expect.True(Agree(crate, 2, 2), Describe(crate));

        // The loser was corrected, and knows it
        Expect.False(crate[2].Object.TryClaim());
    }

    [Test]
    public async Task AGrabBeatsATouch()
    {
        // The client touches the crate twice over (takes it, hands it back) while peer 3, which has not heard, grabs it.
        // The touches reach the host first and count higher; the grab still wins, because ownership is compared first.
        Network.SetLink(1, 2, latencyMs: 10);
        Network.SetLink(1, 3, latencyMs: 100);
        Network.SetLink(2, 3, latencyMs: 100);
        var crate = SpawnEverywhere("Crate");
        await NextFrame();

        Expect.True(crate[1].Object.Authority.Take());
        crate[1].Object.Authority.ReturnToHost();
        Expect.True(crate[2].Object.TryClaim());

        Expect.True(await WaitUntil(() => Agree(crate, 3, 3), 3), Describe(crate));
    }

    [Test]
    public async Task TouchedObjectsFollowTheToucherAndItsStateFlowsFromThere()
    {
        var thrown = SpawnEverywhere("Thrown");
        var hit = SpawnEverywhere("Hit", new Vector3(2, 0, 0));
        await NextFrame();

        // The client throws one crate into another: it takes the first, then the one it hits
        Expect.True(thrown[1].Object.TryClaim());
        thrown[1].Object.ReleaseClaim();
        Expect.True(hit[1].Object.Authority.Take());

        Expect.True(await WaitUntil(() => Agree(thrown, 2, 0) && Agree(hit, 2, 0), 3), Describe(thrown) + " / " + Describe(hit));

        // The host stopped simulating it; the client did not, and the host now shows the client's motion
        var hostTicks = hit[0].Ticks;
        Expect.True(await WaitUntil(() => hit[0].Ticks > hit[1].Ticks - 10 && hit[0].Ticks > hostTicks + 10, 5),
            $"host shows ticks {hit[0].Ticks}, client simulated {hit[1].Ticks}");
    }

    [Test]
    public async Task ARestingObjectGoesBackToTheHost()
    {
        var crate = SpawnEverywhere("Crate", new Vector3(2, 0, 0));
        await NextFrame();

        Expect.True(crate[1].Object.Authority.Take());
        Expect.True(await WaitUntil(() => Agree(crate, 2, 0), 3), Describe(crate));

        crate[1].Object.Authority.ReturnToHost();
        Expect.True(await WaitUntil(() => Agree(crate, 1, 0), 3), Describe(crate));

        var clientTicks = crate[1].Ticks;
        Expect.True(await WaitUntil(() => crate[1].Ticks > clientTicks + 10, 5), $"client shows ticks {crate[1].Ticks}");
    }

    [Test]
    public async Task OnlyTheCurrentAuthorityHandsBack()
    {
        var crate = SpawnEverywhere("Crate");
        await NextFrame();

        Expect.True(crate[1].Object.Authority.Take());
        Expect.True(await WaitUntil(() => Agree(crate, 2, 0), 3), Describe(crate));

        // Peer 3 is not the authority, so it cannot hand the crate to the host
        crate[2].Object.Authority.ReturnToHost();
        for (var i = 0; i < 20; i++) await NextFrame();
        Expect.True(Agree(crate, 2, 0), Describe(crate));
    }

    [Test]
    public async Task StateFromTheFormerAuthorityIsNotShownAfterTheChange()
    {
        Network.LatencyMs = 50;
        var marker = new Vector3(0, 50, 0);
        var crate = SpawnEverywhere("Crate", location: marker);
        Expect.True(await WaitUntil(() => crate[2].Ticks > 5, 5), $"observer shows ticks {crate[2].Ticks}");

        // No teleport: nothing but the authority change itself keeps the two peers' samples apart
        Expect.True(crate[1].Object.Authority.Take());
        crate[1].Location = Vector3.Zero;

        var sawNew = false;
        for (var frame = 0; frame < 150; frame++)
        {
            await NextFrame();
            var y = crate[2].Location.Y;
            Expect.True(y < 1 || y > 49, $"frame {frame}: observer blended the host's samples into the client's, y {y}");
            if (sawNew) Expect.True(y < 1, $"frame {frame}: observer went back to the host's state, y {y}");
            sawNew |= y < 1;
        }
        Expect.True(sawNew, $"observer never showed the client's state: {crate[2].Location}, {Describe(crate)}");
    }

    [Test]
    public async Task WhatALeavingPeerHeldGoesBackToTheHost()
    {
        var crate = SpawnEverywhere("Crate");
        await NextFrame();

        Expect.True(crate[1].Object.TryClaim());
        Expect.True(await WaitUntil(() => Agree(crate, 2, 2), 3), Describe(crate));

        Client.Disconnect();
        HarnessBody[] remaining = [crate[0], crate[2]];
        Expect.True(await WaitUntil(() => Agree(remaining, 1, 0), 3), Describe(remaining));
    }

    [Test]
    public async Task AuthoritySpreadingStopsAtTheSourcesDepthLimit()
    {
        var source = SpawnEverywhere("Source", authority: 2);
        var first = SpawnEverywhere("First");
        var second = SpawnEverywhere("Second");
        foreach (var body in source)
        {
            body.Object.SpreadsAuthority = true;
            body.Object.MaxSpreadDepth = 1;
        }
        foreach (var body in first) body.Object.SpreadsAuthority = true;
        await NextFrame();

        Expect.True(source[1].Object.Spread(first[1].Object));
        Expect.True(await WaitUntil(() => Agree(first, 2, 0), 3), Describe(first));
        Expect.False(first[1].Object.Spread(second[1].Object));
        for (var i = 0; i < 20; i++) await NextFrame();
        Expect.True(Agree(second, 1, 0), Describe(second));
    }

    [Test]
    public async Task FirstOfTwoOpposingChainsTakesBothObjects()
    {
        Network.SetLink(1, 2, latencyMs: 10);
        Network.SetLink(1, 3, latencyMs: 80);
        var left = SpawnEverywhere("Left", authority: 2);
        var right = SpawnEverywhere("Right", authority: 3);
        foreach (var body in left.Concat(right)) body.Object.SpreadsAuthority = true;
        await NextFrame();

        Expect.True(left[1].Object.Spread(right[1].Object));
        Expect.True(right[2].Object.Spread(left[2].Object));

        Expect.True(await WaitUntil(() => Agree(left, 2, 0) && Agree(right, 2, 0), 5),
            Describe(left) + " / " + Describe(right));
    }
}
