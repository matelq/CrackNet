using Godot;

namespace Netfox.Tests;

/// <summary>
/// Two netfox stacks in one SceneTree, talking over a loopback peer. This is the in-process counterpart of
/// examples/e2e: same setup, but assertions instead of a printed line.
/// </summary>
public partial class LoopbackHarnessTests : HarnessSuite
{

    [Test]
    public void EachStackHasItsOwnServers()
    {
        Expect.False(ReferenceEquals(Host.Context, Client.Context));
        Expect.False(ReferenceEquals(Host.Context.NetworkTime, Client.Context.NetworkTime));
        Expect.False(ReferenceEquals(Host.Context.NetworkTime, NetworkTime.Instance));
    }

    [Test]
    public void PeersSeeEachOther()
    {
        Expect.True(Host.Api.IsServer(), "host should be the server");
        Expect.False(Client.Api.IsServer(), "client should not be the server");
        Expect.Equal(1, Host.Api.GetUniqueId());
        Expect.Equal(2, Client.Api.GetUniqueId());
        Expect.SequenceEqual([2], Host.Api.GetPeers());
        Expect.SequenceEqual([1], Client.Api.GetPeers());
    }

    [Test]
    public async Task BothStacksStartTicking()
    {
        var ticking = await WaitUntil(() => Host.Context.NetworkTime.Tick > 0 && Client.Context.NetworkTime.Tick > 0);
        Expect.True(ticking, $"host tick {Host.Context.NetworkTime.Tick}, client tick {Client.Context.NetworkTime.Tick}");
    }

    [Test]
    public async Task ClockSyncBringsTheClientOntoTheHostTick()
    {
        var synced = await WaitUntil(() => Client.Context.NetworkTime.IsInitialSyncDone());
        Expect.True(synced, "client never finished its initial sync");

        var hostTick = Host.Context.NetworkTime.Tick;
        var clientTick = Client.Context.NetworkTime.Tick;
        Expect.True(Math.Abs(hostTick - clientTick) <= 4, $"host at {hostTick}, client at {clientTick}");
    }

    [Test]
    public async Task StateFlowsToTheClientAndInputBackToTheHost()
    {
        var hostPlayers = SpawnPlayers(Host);
        var clientPlayers = SpawnPlayers(Client);

        var states = 0;
        var inputs = 0;
        Client.Context.NetworkSynchronizationServer.OnState += _ => states++;
        Host.Context.NetworkSynchronizationServer.OnInput += _ => inputs++;

        var moved = await WaitUntil(() =>
            states > 0 && inputs > 0 &&
            clientPlayers[1].Position.X > 0 && hostPlayers[2].Position.Z > 0, 5);

        Expect.True(moved,
            $"states={states} inputs={inputs} client Player_1={clientPlayers[1].Position} host Player_2={hostPlayers[2].Position}");

        // The host player is simulated on the host and replicated to the client, never simulated there
        Expect.True(hostPlayers[1].SimulatedTicks > 0, "host should simulate its own player");
        Expect.Equal(0, clientPlayers[1].SimulatedTicks);
    }

    [Test]
    public async Task ClientPositionsConvergeOnTheHostState()
    {
        var hostPlayers = SpawnPlayers(Host);
        var clientPlayers = SpawnPlayers(Client);

        var moved = await WaitUntil(() => clientPlayers[1].Position.X > 0.5f && hostPlayers[1].Position.X > 0.5f, 5);
        Expect.True(moved, $"host {hostPlayers[1].Position}, client {clientPlayers[1].Position}");

        // Both sides agree on where the host player is, up to the ticks the client is behind
        var drift = Math.Abs(hostPlayers[1].Position.X - clientPlayers[1].Position.X);
        var perTick = HarnessPlayer.Speed / Host.Context.NetworkTime.Tickrate;
        Expect.True(drift < perTick * 8, $"drift {drift} over {perTick * 8} (host {hostPlayers[1].Position.X}, client {clientPlayers[1].Position.X})");
    }

    [Test]
    public async Task LateJoiningPeerSyncsAndReceivesState()
    {
        SpawnPlayers(Host);
        SpawnPlayers(Client);
        await WaitUntil(() => Host.Context.NetworkTime.Tick > 5, 5);

        var late = CreateStack("Late", 3);
        Network.ConnectLate(late.Peer);

        var states = 0;
        late.Context.NetworkSynchronizationServer.OnState += _ => states++;
        var latePlayers = SpawnPlayers(late);

        var caughtUp = await WaitUntil(() =>
            late.Context.NetworkTime.IsInitialSyncDone() && states > 0 && latePlayers[1].Position.X > 0, 5);

        Expect.True(caughtUp,
            $"synced={late.Context.NetworkTime.IsInitialSyncDone()} states={states} Player_1={latePlayers[1].Position}");
    }

    [Test]
    public async Task PacketLossDoesNotStopTheClient()
    {
        Network.PacketLoss = 0.3;
        Network.LatencyMs = 20;

        var hostPlayers = SpawnPlayers(Host);
        var clientPlayers = SpawnPlayers(Client);

        var moved = await WaitUntil(() => clientPlayers[1].Position.X > 0.5f && hostPlayers[2].Position.Z > 0.5f, 8);
        Expect.True(moved,
            $"client Player_1={clientPlayers[1].Position} host Player_2={hostPlayers[2].Position} under 30% loss");
    }

    [Test]
    public async Task StateIsNotResentForEveryResimulatedTick()
    {
        // Latency well past the input delay: every client input lands on an already simulated tick, so the host
        // resimulates a range every frame. Sending state for each of those ticks is what upstream #630 is about.
        Network.LatencyMs = 150;

        SpawnPlayers(Host);
        var clientPlayers = SpawnPlayers(Client);

        var states = 0;
        Client.Context.NetworkSynchronizationServer.OnState += _ => states++;

        await WaitUntil(() => clientPlayers[1].Position.X > 0.3f, 6);

        var firstTick = Host.Context.NetworkTime.Tick;
        var firstStates = states;
        await WaitUntil(() => Host.Context.NetworkTime.Tick - firstTick > 30, 6);

        var ticks = Host.Context.NetworkTime.Tick - firstTick;
        var packets = states - firstStates;

        // The ceiling is one packet per subject per tick: each subject is sent once per loop, at the newest tick it
        // is authoritative for, and two subjects driven by different peers rarely share that tick. What this rules
        // out is the range length multiplying it - at 150ms the host resimulates about five ticks every frame.
        var subjects = Host.Context.NetworkSynchronizationServer.OwnedRollbackStateProperties.Subjects.Count;
        Expect.True(packets <= ticks * subjects,
            $"{packets} state packets for {ticks} host ticks over {subjects} subjects: state is being re-sent for resimulated ticks");
    }

    /// <summary>
    /// The peer driving a node has to receive the authority's state for it, and that is not the same question as
    /// whether state arrives at all.
    /// <para>
    /// State is only sent for subjects the sender is authoritative for, and the authority is <i>predicting</i> a node
    /// driven by a remote peer on the newest tick - that peer's input has not arrived yet. So the newest tick alone
    /// carries every node except the ones whose drivers most need it, and a client can receive thousands of updates
    /// about everyone else while never being told once where its own player is (netfox-net#35).
    /// </para>
    /// </summary>
    [Test]
    public async Task StateReachesThePeerDrivingTheNode()
    {
        Network.LatencyMs = 150;

        SpawnPlayers(Host);
        var clientPlayers = SpawnPlayers(Client);

        var ownStates = 0;
        var otherStates = 0;
        Client.Context.NetworkSynchronizationServer.OnState += snapshot =>
        {
            if (snapshot.TryGetProperty(clientPlayers[2], "position", out _)) ownStates++;
            if (snapshot.TryGetProperty(clientPlayers[1], "position", out _)) otherStates++;
        };

        await WaitUntil(() => ownStates > 5 && otherStates > 5, 8);

        Expect.True(otherStates > 5, $"client received {otherStates} states for the host's player");
        Expect.True(ownStates > 5,
            $"client received {otherStates} states for the host's player and {ownStates} for its own: the node it " +
            "drives is predicted on the newest tick, so only sending that tick never tells it anything");
    }

    [Test]
    public async Task ASecondSessionOnTheSameServersWorks()
    {
        var hostPlayers = SpawnPlayers(Host);
        var clientPlayers = SpawnPlayers(Client);
        Expect.True(await WaitUntil(() => clientPlayers[1].Position.X > 0.3f, 5), "first session never replicated");

        // Run past the history limit, so the tick indexed buffers hold ticks the next session will never reach
        var historyLimit = NetfoxSettings.Instance.RollbackHistoryLimit;
        Expect.True(await WaitUntil(() => Host.Context.NetworkTime.Tick > historyLimit + 20, 8),
            $"first session only reached tick {Host.Context.NetworkTime.Tick}");

        // Leave the lobby: peers go away and the level is torn down, but the servers live on
        Host.Disconnect();
        Client.Disconnect();
        foreach (var player in hostPlayers.Values.Concat(clientPlayers.Values))
        {
            player.GetParent().RemoveChild(player);
            player.Free();
        }
        await NextFrame();
        await NextFrame();

        // Join another lobby with the same stacks
        ReplaceNetwork(new LoopbackNetwork());
        Host.Reconnect(Network, 1);
        Client.Reconnect(Network, 2);
        Network.Connect();
        await NextFrame();

        SpawnPlayers(Host);
        var clientPlayers2 = SpawnPlayers(Client);

        Expect.True(await WaitUntil(() => clientPlayers2[1].Position.X > 0.3f, 6),
            $"second session did not replicate: client Player_1={clientPlayers2[1].Position}");
    }

    [Test]
    public async Task ASecondSessionWithTheSameNodesWorks()
    {
        var hostPlayers = SpawnPlayers(Host);
        var clientPlayers = SpawnPlayers(Client);
        Expect.True(await WaitUntil(() => clientPlayers[1].Position.X > 0.3f, 5), "first session never replicated");

        // Stand in for a long session: history ends up far above the tick the next session will start at
        Host.Context.NetworkTime.SetTick(2000);
        Client.Context.NetworkTime.SetTick(2000);
        Expect.True(await WaitUntil(() => Host.Context.NetworkTime.Tick > 2010, 8),
            $"first session only reached tick {Host.Context.NetworkTime.Tick}");

        // Leave the lobby, but keep the scene: the nodes, their synchronizers and their history all survive
        Host.Disconnect();
        Client.Disconnect();
        await NextFrame();
        await NextFrame();

        var restartFrom = clientPlayers[1].Position.X;

        ReplaceNetwork(new LoopbackNetwork());
        Host.Reconnect(Network, 1);
        Client.Reconnect(Network, 2);
        Network.Connect();

        var states = 0;
        Client.Context.NetworkSynchronizationServer.OnState += _ => states++;

        Expect.True(await WaitUntil(() => clientPlayers[1].Position.X > restartFrom + 0.3f, 8),
            $"second session did not replicate with the same nodes: client Player_1={clientPlayers[1].Position}, " +
            $"host Player_1={hostPlayers[1].Position}, host tick {Host.Context.NetworkTime.Tick}, states received {states}, " +
            $"host owns {Host.Context.NetworkSynchronizationServer.OwnedRollbackStateProperties.Subjects.Count} subjects");
    }

    [Test]
    public async Task BandwidthAtPlayerScale()
    {
        // Without latency the host simulates each tick once; with it, every late input makes it resimulate a range,
        // which is where state traffic used to multiply (#29)
        var idle = await MeasureBandwidth(0);
        var lagging = await MeasureBandwidth(100);

        Report("no latency", idle);
        Report("100ms latency", lagging);

        static void Report(string label, (double HostKbps, double ClientKbps, double PerPlayerTick, double HostPps, double HostPacket, double ClientPps, double ClientPacket) m)
        {
            var host = FormattableString.Invariant(
                $"host {m.HostKbps:F1}KB/s = {m.HostPps:F0} packets/s x {m.HostPacket:F0}B ({m.PerPlayerTick:F0}B per player per tick)");
            var client = FormattableString.Invariant(
                $"client {m.ClientKbps:F1}KB/s = {m.ClientPps:F0} packets/s x {m.ClientPacket:F0}B");
            GD.Print($"BANDWIDTH 2 moving players, {label}: {host}, {client}");
        }

        // State is sent once per loop, so resimulating a range must not multiply what goes out
        Expect.True(lagging.PerPlayerTick < idle.PerPlayerTick * 2,
            $"latency should not multiply state traffic: {lagging.PerPlayerTick:F0}B per player per tick against {idle.PerPlayerTick:F0}B without latency");
    }

    private async Task<(double HostKbps, double ClientKbps, double PerPlayerTick, double HostPps, double HostPacket, double ClientPps, double ClientPacket)> MeasureBandwidth(int latencyMs)
    {
        foreach (var stack in new[] { Host, Client })
            foreach (var child in stack.GetChildren())
                if (child is HarnessPlayer player)
                {
                    stack.RemoveChild(player);
                    player.Free();
                }

        Network.LatencyMs = latencyMs;
        SpawnPlayers(Host);
        SpawnPlayers(Client);
        await WaitUntil(() => Client.Context.NetworkTime.IsInitialSyncDone(), 6);

        var firstTick = Host.Context.NetworkTime.Tick;
        Network.ResetTraffic();
        var start = Time.GetTicksMsec();
        await WaitUntil(() => Host.Context.NetworkTime.Tick - firstTick > 60, 8);

        var seconds = (Time.GetTicksMsec() - start) / 1000.0;
        var ticks = Math.Max(1, Host.Context.NetworkTime.Tick - firstTick);
        var fromHost = Network.TrafficFrom(1);
        var fromClient = Network.TrafficFrom(2);

        return (fromHost.Bytes / seconds / 1024, fromClient.Bytes / seconds / 1024, fromHost.Bytes / (double)ticks / 2,
            fromHost.Packets / seconds, fromHost.Bytes / (double)Math.Max(1, fromHost.Packets),
            fromClient.Packets / seconds, fromClient.Bytes / (double)Math.Max(1, fromClient.Packets));
    }

    /// <summary>
    /// Upstream foxssake/netfox#613: with prediction on, a player is reported to keep moving after letting go of the
    /// button and then snap back. Packet fragmentation is in our port too, so the symptom would be ours as well.
    /// </summary>
    [Test]
    public async Task PredictedPlayerStopsWhenTheInputStops()
    {
        Network.LatencyMs = 40;

        var hostPlayers = SpawnPlayers(Host, enablePrediction: true);
        var clientPlayers = SpawnPlayers(Client, enablePrediction: true);

        Expect.True(await WaitUntil(() => hostPlayers[2].Position.Z > 0.5f, 6),
            $"the client player never got moving on the host: {hostPlayers[2].Position}");

        // The client lets go. Its own input stops immediately; the host learns about it one latency later.
        clientPlayers[2].Input.Held = false;
        var releasedAt = Host.Context.NetworkTime.Tick;
        var releasedFrom = hostPlayers[2].Position.Z;

        // Give the release time to arrive and any prediction to be corrected
        await WaitUntil(() => Host.Context.NetworkTime.Tick - releasedAt > 20, 4);

        var settled = hostPlayers[2].Position.Z;
        var perTick = HarnessPlayer.Speed / Host.Context.NetworkTime.Tickrate;

        // Some overshoot is expected: input in flight plus the input delay. Running on is not.
        Expect.True(settled - releasedFrom < perTick * 12,
            FormattableString.Invariant(
                $"host kept moving the player {settled - releasedFrom:F3} past the release, over {perTick * 12:F3}"));

        // And once settled it must stay put, rather than creep forward on predicted input
        var before = hostPlayers[2].Position.Z;
        var at = Host.Context.NetworkTime.Tick;
        await WaitUntil(() => Host.Context.NetworkTime.Tick - at > 20, 4);

        Expect.True(Math.Abs(hostPlayers[2].Position.Z - before) < perTick,
            FormattableString.Invariant(
                $"host player drifted {hostPlayers[2].Position.Z - before:F3} while the client held no input"));

        // Without this the case proves nothing about #613: the host has to have run at least one predicted tick
        Expect.True(hostPlayers[2].PredictedTicks > 0,
            $"the host never predicted the client player, so this case says nothing about prediction");

        // The client's own view has to agree, or it is the snapping back from the report
        Expect.True(Math.Abs(clientPlayers[2].Position.Z - hostPlayers[2].Position.Z) < perTick * 8,
            FormattableString.Invariant(
                $"client sees {clientPlayers[2].Position.Z:F3}, host {hostPlayers[2].Position.Z:F3}"));
    }

    /// <summary>The same release, with the packets it depends on going missing: prediction must still converge.</summary>
    [Test]
    public async Task PredictedPlayerStopsWhenTheInputStopsUnderLoss()
    {
        Network.LatencyMs = 40;
        Network.PacketLoss = 0.2;

        var hostPlayers = SpawnPlayers(Host, enablePrediction: true);
        var clientPlayers = SpawnPlayers(Client, enablePrediction: true);

        Expect.True(await WaitUntil(() => hostPlayers[2].Position.Z > 0.5f, 8),
            $"the client player never got moving on the host: {hostPlayers[2].Position}");

        clientPlayers[2].Input.Held = false;
        var releasedAt = Host.Context.NetworkTime.Tick;
        await WaitUntil(() => Host.Context.NetworkTime.Tick - releasedAt > 40, 6);

        var before = hostPlayers[2].Position.Z;
        var at = Host.Context.NetworkTime.Tick;
        await WaitUntil(() => Host.Context.NetworkTime.Tick - at > 20, 4);

        var perTick = HarnessPlayer.Speed / Host.Context.NetworkTime.Tickrate;
        Expect.True(hostPlayers[2].PredictedTicks > 0, "the host never predicted the client player");
        Expect.True(Math.Abs(hostPlayers[2].Position.Z - before) < perTick,
            FormattableString.Invariant(
                $"host player drifted {hostPlayers[2].Position.Z - before:F3} after the release, under 20% loss"));
    }

    /// <summary>
    /// Upstream foxssake/netfox#236: with a second input node owned by the server, the client is reported to treat its
    /// state as fully server-determined and stop taking its own input into account.
    /// </summary>
    [Test]
    public async Task InputNodesWithDifferentAuthoritiesStillLetTheClientSimulate()
    {
        Network.LatencyMs = 40;

        var hostPlayer = HarnessPlayer.Spawn(Host, 2, withServerEvents: true);
        var clientPlayer = HarnessPlayer.Spawn(Client, 2, withServerEvents: true);

        Expect.True(await WaitUntil(() => hostPlayer.Position.Z > 0.5f, 6),
            $"the player never got moving on the host: {hostPlayer.Position}");

        // The client owns one of the two input nodes, so it has to keep simulating its own player rather than
        // waiting for the host to tell it where it is
        Expect.True(clientPlayer.SimulatedTicks > 0,
            "the client stopped simulating its own player once a server-owned input node was added");

        // And the state it is told about must not run away from the ticks it is simulating
        var stateAge = clientPlayer.Synchronizer.GetLastKnownState();
        Expect.True(stateAge >= 0, "the client never received state for its player");

        var perTick = HarnessPlayer.Speed / Host.Context.NetworkTime.Tickrate;
        Expect.True(Math.Abs(clientPlayer.Position.Z - hostPlayer.Position.Z) < perTick * 10,
            FormattableString.Invariant(
                $"client sees {clientPlayer.Position.Z:F3}, host {hostPlayer.Position.Z:F3}, state age {stateAge}"));
    }

    private static Dictionary<int, HarnessPlayer> SpawnPlayers(NetfoxStack stack, bool enablePrediction = false) => new()
    {
        [1] = HarnessPlayer.Spawn(stack, 1, enablePrediction),
        [2] = HarnessPlayer.Spawn(stack, 2, enablePrediction),
    };
}
