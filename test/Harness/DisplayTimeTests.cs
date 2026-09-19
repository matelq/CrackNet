using Godot;

namespace CrackNet.Tests;

/// <summary>
/// What one display time per screen costs (#78). A screen draws every remote object at the deepest of its live links,
/// so a guest on a bad line ages every other player on that screen, and the moment it joins the screen holds still
/// until the common time has walked down to it. Both are measured here rather than argued about.
/// <para>
/// The bound is not the link's nominal latency: in this harness four stacks share one tree and packets are delivered
/// on frames, so a 300 ms link reads about half as deep again. What is asserted is the model's own statement - a good
/// link's player is drawn at the deep peer's depth, not at its own - which holds whatever the harness adds to both.
/// </para>
/// </summary>
public partial class DisplayTimeTests : HarnessSuite
{
    private static readonly Vector3 Walk = new(3, 0, 0);

    /// <summary>A good link and a bad one, far enough apart that the cost cannot be read as jitter.</summary>
    private const int GoodMs = 25, BadMs = 300;

    /// <summary>Clock sync, the send interval and a harness frame blur every reading by a few ticks.</summary>
    private const double ToleranceMs = 80;

    [Test]
    public async Task ADeepLinkAgesEveryOtherPlayerOnTheScreen()
    {
        Network.LatencyMs = GoodMs;
        var walkers = new[] { Host, Client }.Select(stack => HarnessWorld.Walker(stack, 2, new Vector3(-8, 1, 0), Walk)).ToArray();
        Expect.True(await WaitUntil(() => Host.Context.NetworkObjectServer.GetPlaybackStatus(2) is not null
                                          && walkers[0].GlobalPosition.X > -7, 8), "the host never saw the client's walker move");
        for (var i = 0; i < 20; i++) await NextFrame();

        // How far behind its own peer the host draws it, per rendered frame, in milliseconds of its own walk
        var alone = await BehindMs(walkers[0], walkers[1], 30);
        var ageAlone = DepthMs(2);

        // A third player on a bad line, talking to both of the others over it
        Network.SetLink(1, 3, BadMs);
        Network.SetLink(2, 3, BadMs);
        var deep = AddPeer(3);
        Expect.True(await WaitUntil(() => deep.Context.NetworkTime.IsInitialSyncDone(), 8), "the third peer never synced");
        foreach (var stack in new[] { Host, Client, deep })
            HarnessWorld.Walker(stack, 3, new Vector3(-8, 1, 4), Walk);

        // The pause: the common time stops where it is until the deep clock has run down to it, and everything the
        // host draws stands still for that long. Watched on the walker it already had, which never stopped walking
        Expect.True(await WaitUntil(() => Host.Context.NetworkObjectServer.GetPlaybackStatus(3) is not null, 8),
            "the host never heard the deep peer");
        var stalled = await LongestStillMs(walkers[0]);

        Expect.True(await WaitUntil(() => DepthMs(3) > ageAlone + 150, 8), "the deep peer never read as deeper than the good one");
        for (var i = 0; i < 90; i++) await NextFrame();

        var together = await BehindMs(walkers[0], walkers[1], 30);
        var status = Host.Context.NetworkObjectServer.GetPlaybackStatus(3)!.Value;
        var ageDeep = DepthMs(3);
        var ageGood = DepthMs(2);
        var apart = ageDeep - ageGood;

        var report = $"the {GoodMs} ms player is drawn {alone:F0} ms behind while it is the only one, and {together:F0} ms behind " +
                     $"once a {BadMs} ms player is on the screen: it pays {together - alone:F0} ms it has no link for. " +
                     $"The deep peer's own depth is {ageDeep:F0} ms ({status.NetworkMs:F0} ms of link, " +
                     $"{status.PlaybackTicks * 1000 / Host.Context.NetworkTime.Tickrate:F0} ms of buffer), the good peer's {ageGood:F0} ms. " +
                     $"The screen held still {stalled:F0} ms when the deep peer joined. The depths are {apart:F0} ms apart, " +
                     $"which per-peer times would show as {apart * 3 / 1000:F2} m of a rider misplaced along a 3 m/s platform, " +
                     $"and times grouped by what interacts as {apart * 6 / 1000:F2} m of retiming when a 6 m/s runner steps on";
        GD.Print("DISPLAY TIME " + report);

        // The model, stated as what is drawn: alone, the good peer is shown at its own depth; with the deep peer
        // there, at the deep peer's. Nearly all of that depth is link, so no tuning of ours takes the cost away
        Expect.True(Math.Abs(alone - ageAlone) < ToleranceMs, "the good peer alone is not drawn at its own depth: " + report);
        Expect.True(Math.Abs(together - ageDeep) < ToleranceMs, "the good peer is no longer drawn at the deep peer's depth: " + report);
        Expect.True(stalled > 50, "the screen no longer pauses when a deeper peer joins: " + report);
    }

    /// <summary>How old what a peer sends is on the host, in milliseconds, link and buffer together.</summary>
    private double DepthMs(int peer)
        => Host.Context.NetworkObjectServer.GetPlaybackStatus(peer) is { } status
            ? status.TotalTicks * 1000 / Host.Context.NetworkTime.Tickrate
            : 0;

    /// <summary>
    /// How far behind its authority an observer draws a body, in milliseconds of its walk, averaged over rendered
    /// frames. Metres over the walk's speed rather than ticks: what a player sees is the distance, and the two peers'
    /// bodies live in separate worlds at the same coordinates.
    /// </summary>
    private async Task<double> BehindMs(Node3D observed, Node3D own, int frames)
    {
        var behind = new List<float>();
        var drawn = new DrawnFrame();
        AddChild(drawn);
        drawn.Drawn += () => behind.Add(own.GlobalPosition.X - observed.GlobalPosition.X);
        for (var i = 0; i < frames; i++) await NextFrame();
        drawn.QueueFree();
        Expect.True(behind.Count > frames / 2, $"only {behind.Count} frames drawn");
        return behind.Average() / Walk.X * 1000;
    }

    /// <summary>The longest run of rendered frames a body was drawn in the same place, in milliseconds.</summary>
    private async Task<double> LongestStillMs(Node3D observed)
    {
        var longest = 0.0;
        var still = 0.0;
        var at = observed.GlobalPosition.X;
        var drawn = new DrawnFrame();
        AddChild(drawn);
        drawn.Drawn += () =>
        {
            still = Mathf.Abs(observed.GlobalPosition.X - at) < 0.001f ? still + GetProcessDeltaTime() * 1000 : 0;
            at = observed.GlobalPosition.X;
            longest = Math.Max(longest, still);
        };
        for (var i = 0; i < 60; i++) await NextFrame();
        drawn.QueueFree();
        return longest;
    }
}
