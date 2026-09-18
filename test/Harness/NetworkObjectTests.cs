using Godot;

namespace CrackNet.Tests;

/// <summary>State of a <see cref="NetworkObject"/> goes from its authority to everyone else and plays back smoothly.</summary>
public partial class NetworkObjectTests : HarnessSuite
{
    private static readonly Vector3 Speed = new(3, 0, 0);

    [Test]
    public async Task PlaybackReadoutSeparatesNetworkAndPlaybackDelay()
    {
        // 100 ms each way at 30 Hz: state is about three ticks old when it arrives, then waits in the playback buffer
        Network.LatencyMs = 100;
        HarnessBody.Place(Host, "Clock", 1, Speed);
        HarnessBody.Place(Client, "Clock", 1, Speed);

        Expect.True(await WaitUntil(() => Client.Context.NetworkTime.IsInitialSyncDone()
                                          && Client.Context.NetworkObjectServer.GetPlaybackStatus(1) is not null, 8),
            "client never synced and observed the host clock");
        for (var i = 0; i < 60; i++) await NextFrame();

        var status = Client.Context.NetworkObjectServer.GetPlaybackStatus(1)!.Value;
        var interval = NetworkObjectServer.StateIntervalTicks;

        // Clock sync error blurs both by a couple of ticks; a readout that swapped or summed the parts, or ignored the
        // latency, falls outside
        Expect.True(status.NetworkTicks >= 1 && status.NetworkTicks <= 3 + interval + 2,
            $"network age {status.NetworkTicks:F1} ticks for 100 ms of latency");
        Expect.True(status.PlaybackTicks <= Client.Context.NetworkObjectServer.PlaybackDelayTicks + interval + 2,
            $"playback age {status.PlaybackTicks:F1} ticks");
    }

    [Test]
    public async Task ARestingPeerDoesNotReadAsLate()
    {
        // No latency, and the host's body never changes: it sends only a heartbeat a second. Measured against the
        // newest tick that read as up to a second behind.
        HarnessBody.Place(Host, "Resting", 1).CountsTicks = false;
        HarnessBody.Place(Client, "Resting", 1).CountsTicks = false;

        Expect.True(await WaitUntil(() => Client.Context.NetworkTime.IsInitialSyncDone()
                                          && Client.Context.NetworkObjectServer.GetPlaybackStatus(1) is not null, 8),
            "client never synced and observed the host clock");
        for (var i = 0; i < 150; i++) await NextFrame();

        var status = Client.Context.NetworkObjectServer.GetPlaybackStatus(1)!.Value;
        Expect.True(status.TotalTicks <= Client.Context.NetworkObjectServer.PlaybackDelayTicks + NetworkObjectServer.StateIntervalTicks + 3,
            $"a resting host reads {status.TotalTicks:F1} ticks behind with no latency");
    }

    [Test]
    public async Task HostObjectPlaysBackOnTheClientBehindTheHost()
    {
        var onHost = HarnessBody.Place(Host, "Crate", 1, Speed);
        var onClient = HarnessBody.Place(Client, "Crate", 1, Speed);

        var arrived = await WaitUntil(() => onClient.Location.X > 1, 5);
        Expect.True(arrived, $"client {onClient.Location}, host {onHost.Location}");
        Expect.True(onClient.Ticks > 0 && onClient.Ticks <= onHost.Ticks, $"ticks client {onClient.Ticks} host {onHost.Ticks}");

        // Behind, but only by the playback delay plus a few ticks of transit and frame granularity
        var tickrate = Host.Context.NetworkTime.Tickrate;
        var lag = onHost.Location.X - onClient.Location.X;
        var bound = (Client.Context.NetworkObjectServer.PlaybackDelayTicks + 6) * Speed.X / tickrate;
        Expect.True(lag > 0 && lag < bound, $"lag {lag}, bound {bound}");
    }

    [Test]
    public async Task ObjectsOfOnePeerAreShownAtTheSameTick()
    {
        HarnessBody.Place(Host, "Below", 1, Speed);
        HarnessBody.Place(Host, "Above", 1, Speed, new Vector3(0, 1, 0));
        var below = HarnessBody.Place(Client, "Below", 1, Speed);
        Expect.True(await WaitUntil(() => below.Location.X > 0.5f, 5), $"below {below.Location}");

        // The second one starts receiving much later: a clock per object would show it at a different moment
        var above = HarnessBody.Place(Client, "Above", 1, Speed, new Vector3(0, 1, 0));
        Expect.True(await WaitUntil(() => above.Location.X > below.Location.X - 0.01f, 5), $"above {above.Location}");

        // Every displayed frame, the stack is exactly one above the other: never a tick apart
        for (var frame = 0; frame < 60; frame++)
        {
            await NextFrame();
            Expect.Approx(below.Location.X, above.Location.X, 1e-4, $"frame {frame}: below {below.Location}, above {above.Location}");
        }
    }

    [Test]
    public async Task ClientObjectReachesTheHostAndAThirdPeer()
    {
        HarnessBody.Place(Host, "Ball", 2);
        HarnessBody.Place(Client, "Ball", 2, Speed);
        var third = AddPeer(3);
        var onThird = HarnessBody.Place(third, "Ball", 2);
        var onHost = (HarnessBody)Host.GetNode("Ball");

        var arrived = await WaitUntil(() => onHost.Location.X > 1 && onThird.Location.X > 1, 5);
        Expect.True(arrived, $"host {onHost.Location}, third {onThird.Location}");
    }

    [Test]
    public async Task PlaybackIsMonotonicAndSmoothUnderLossAndLatency()
    {
        Network.LatencyMs = 40;
        Network.PacketLoss = 0.2;
        HarnessBody.Place(Host, "Crate", 1, Speed);
        var onClient = HarnessBody.Place(Client, "Crate", 1, Speed);

        Expect.True(await WaitUntil(() => onClient.Location.X > 0.5f, 8), $"client {onClient.Location}");

        var previous = onClient.Location.X;
        var biggest = 0f;
        for (var frame = 0; frame < 120; frame++)
        {
            await NextFrame();
            var step = onClient.Location.X - previous;
            Expect.True(step >= 0, $"frame {frame}: moved back by {-step}");
            biggest = Math.Max(biggest, step);
            previous = onClient.Location.X;
        }

        // At most a couple of ticks' worth of motion in one frame: a snap would be the whole buffer at once
        var perTick = Speed.X / Host.Context.NetworkTime.Tickrate;
        Expect.True(biggest < perTick * 2.5f, $"biggest step {biggest}, per tick {perTick}");
    }

    [Test]
    public async Task ASnapDoesNotFlyThroughTheMap()
    {
        var onHost = HarnessBody.Place(Host, "Player", 1, Speed);
        var onClient = HarnessBody.Place(Client, "Player", 1, Speed);
        Expect.True(await WaitUntil(() => onClient.Location.X > 0.3f, 5), $"client {onClient.Location}");

        onHost.Location = new Vector3(1000, 0, 0);
        onHost.Object.Snap();

        var inBetween = 0;
        var landed = false;
        for (var frame = 0; frame < 180 && !landed; frame++)
        {
            await NextFrame();
            var x = onClient.Location.X;
            if (x > 20 && x < 990) inBetween++;
            landed = x >= 1000;
        }

        Expect.True(landed, $"client {onClient.Location}");
        Expect.Equal(0, inBetween);
    }

    [Test]
    public async Task AnObjectThatRestedStartsMovingWhenItsAuthorityDid()
    {
        var rest = new Vector3(5, 0, 0);
        var onHost = HarnessBody.Place(Host, "Crate", 1, Vector3.Zero, rest);
        var onClient = HarnessBody.Place(Client, "Crate", 1, Vector3.Zero, rest);
        onHost.CountsTicks = onClient.CountsTicks = false;
        Expect.True(await WaitUntil(() => onClient.Visible, 5), "never shown");

        // Long enough at rest that the host stops sending anything but heartbeats
        for (var i = 0; i < 90; i++) await NextFrame();
        var startedAt = Host.Context.NetworkTime.Tick;
        onHost.Velocity = Speed;

        // Motion on the client starts when playback reaches the tick the host started at, not by creeping over the
        // whole rest as if the crate had been moving since its last heartbeat
        var previous = onClient.Location.X;
        var moved = false;
        for (var frame = 0; frame < 120; frame++)
        {
            await NextFrame();
            if (onClient.Location.X > previous && !moved)
            {
                moved = true;
                var shown = Client.Context.NetworkObjectServer.Diagnostics.GetDisplayTick(1) ?? -1;
                Expect.True(shown >= startedAt - NetworkObjectServer.StateIntervalTicks,
                    $"client started moving at display tick {shown:F1}, the host at {startedAt}");
            }
            previous = onClient.Location.X;
        }
        Expect.True(moved, $"client never moved: {onClient.Location}");
    }
}
