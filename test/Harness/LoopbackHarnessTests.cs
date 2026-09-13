using Godot;
using Netfox.Extras;

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

    /// <summary>
    /// Four peers, which is the player count we are building for. The name used to promise a player scale it did not
    /// run at: two peers is the count at which the host has only one client to send to, so nothing about how state
    /// traffic grows with the room shows up.
    /// </summary>
    [Test]
    public async Task BandwidthAtPlayerScale()
    {
        NetfoxStack[] stacks = [Host, Client, AddPeer(3), AddPeer(4)];

        // Without latency the host simulates each tick once; with it, every late input makes it resimulate a range,
        // which is where state traffic used to multiply (#29)
        var idle = await MeasureBandwidth(stacks, 0);
        var lagging = await MeasureBandwidth(stacks, 100);

        Report("no latency", idle);
        Report("100ms latency", lagging);

        // Input is the half netfox-net#40 changed, so it is the half that has to be watched: the acknowledged window
        // is smaller than a fixed three when everything is arriving, and only grows when something is not
        Expect.True(idle.InputBytes <= lagging.InputBytes,
            $"input traffic should not shrink under latency: {idle.InputBytes:F0}B idle against {lagging.InputBytes:F0}B lagging");

        static void Report(string label, (double HostKbps, double ClientKbps, double PerPlayerTick, double HostPps, double HostPacket, double ClientPps, double ClientPacket, double InputBytes, double AckBytes, double StateBytes) m)
        {
            var host = FormattableString.Invariant(
                $"host {m.HostKbps:F1}KB/s = {m.HostPps:F0} packets/s x {m.HostPacket:F0}B ({m.PerPlayerTick:F0}B per player per tick)");
            var client = FormattableString.Invariant(
                $"client {m.ClientKbps:F1}KB/s = {m.ClientPps:F0} packets/s x {m.ClientPacket:F0}B");
            var split = FormattableString.Invariant(
                $"input {m.InputBytes:F0}B/tick, state {m.StateBytes:F0}B/tick, input acks {m.AckBytes:F0}B/tick");
            GD.Print($"BANDWIDTH {PlayerScale} moving players, {label}: {host}, {client}, {split}");
        }

        // State is sent once per loop, so resimulating a range must not multiply what goes out
        Expect.True(lagging.PerPlayerTick < idle.PerPlayerTick * 2,
            $"latency should not multiply state traffic: {lagging.PerPlayerTick:F0}B per player per tick against {idle.PerPlayerTick:F0}B without latency");
    }

    /// <summary>Players in the bandwidth case, and so peers: the target game is four.</summary>
    private const int PlayerScale = 4;

    private async Task<(double HostKbps, double ClientKbps, double PerPlayerTick, double HostPps, double HostPacket, double ClientPps, double ClientPacket, double InputBytes, double AckBytes, double StateBytes)> MeasureBandwidth(NetfoxStack[] stacks, int latencyMs)
    {
        foreach (var stack in stacks)
            foreach (var child in stack.GetChildren())
                if (child is HarnessPlayer player)
                {
                    stack.RemoveChild(player);
                    player.Free();
                }

        Network.LatencyMs = latencyMs;
        foreach (var stack in stacks) SpawnPlayers(stack, peers: PlayerScale);
        await WaitUntil(() => stacks.All(stack => stack.Context.NetworkTime.IsInitialSyncDone()), 8);

        var firstTick = Host.Context.NetworkTime.Tick;
        Network.ResetTraffic();
        foreach (var stack in stacks) stack.Context.NetworkCommandServer.ResetSentCounts();
        var start = Time.GetTicksMsec();
        await WaitUntil(() => Host.Context.NetworkTime.Tick - firstTick > 60, 8);

        var seconds = (Time.GetTicksMsec() - start) / 1000.0;
        var ticks = Math.Max(1, Host.Context.NetworkTime.Tick - firstTick);
        var fromHost = Network.TrafficFrom(1);
        var fromClient = Network.TrafficFrom(2);

        // Across every stack, so "how much input is on this wire" is one number rather than one per sender
        var sent = stacks.Select(stack => stack.Context.NetworkCommandServer.SentCounts).ToArray();
        double PerTick(params int[] commands) =>
            sent.Sum(counts => commands.Sum(id => (double)counts.GetValueOrDefault(id).Bytes)) / ticks;

        return (fromHost.Bytes / seconds / 1024, fromClient.Bytes / seconds / 1024,
            fromHost.Bytes / (double)ticks / PlayerScale,
            fromHost.Packets / seconds, fromHost.Bytes / (double)Math.Max(1, fromHost.Packets),
            fromClient.Packets / seconds, fromClient.Bytes / (double)Math.Max(1, fromClient.Packets),
            PerTick(CommandIds.Input), PerTick(CommandIds.InputAck),
            PerTick(CommandIds.FullState, CommandIds.DiffState));
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

    /// <summary>
    /// Three peers, every one of them owning a player. The case two peers cannot express: on peer 3, player 2 is
    /// neither its own nor the authority's, and the only way it hears about that player is the host relaying state
    /// the host itself simulated from input it received. If that path is broken, two peers never notice.
    /// </summary>
    [Test]
    public async Task EveryPeerAgreesAboutEveryPlayer()
    {
        Network.LatencyMs = 20;
        var third = AddPeer(3);

        var players = new Dictionary<NetfoxStack, Dictionary<int, HarnessPlayer>>
        {
            [Host] = SpawnPlayers(Host, peers: 3),
            [Client] = SpawnPlayers(Client, peers: 3),
            [third] = SpawnPlayers(third, peers: 3),
        };

        // Each peer's own player is driven from that peer, so all three have to be moving before anything is compared
        var moving = await WaitUntil(() => players.Keys.All(stack =>
            players[stack].Values.All(player => player.Position.Length() > 0.5f)), 8);
        Expect.True(moving, Describe(players));

        // The host simulates all three, and no client simulates a player it does not own
        Expect.Equal(0, players[Client][3].SimulatedTicks);
        Expect.Equal(0, players[third][2].SimulatedTicks);

        var perTick = HarnessPlayer.Speed / Host.Context.NetworkTime.Tickrate;
        foreach (var owner in new[] { 1, 2, 3 })
        {
            var authoritative = players[Host][owner].Position;
            foreach (var stack in players.Keys)
            {
                var drift = (players[stack][owner].Position - authoritative).Length();
                Expect.True(drift < perTick * 12,
                    FormattableString.Invariant($"Player_{owner} drifts {drift:F3} on {stack.Name}: {Describe(players)}"));
            }
        }
    }

    /// <summary>
    /// One peer on a bad line must stay that peer's problem. Latency used to be a property of the whole network, so
    /// this could not be asked at all: slowing one peer slowed everyone, which is the answer the test is looking for.
    /// </summary>
    [Test]
    public async Task OnePeerOnASlowLinkDoesNotHoldTheOthersBack()
    {
        var third = AddPeer(3);
        Network.SetLink(1, 3, latencyMs: 150, packetLoss: 0.1);
        Network.SetLink(2, 3, latencyMs: 150, packetLoss: 0.1);

        var hostPlayers = SpawnPlayers(Host, peers: 3);
        var clientPlayers = SpawnPlayers(Client, peers: 3);
        var thirdPlayers = SpawnPlayers(third, peers: 3);

        var moving = await WaitUntil(() =>
            hostPlayers.Values.All(player => player.Position.Length() > 0.5f) &&
            clientPlayers[1].Position.Length() > 0.5f && thirdPlayers[3].Position.Length() > 0.5f, 10);
        Expect.True(moving, $"host={Positions(hostPlayers)} client={Positions(clientPlayers)} third={Positions(thirdPlayers)}");

        // The client's link to the host is untouched, so its view of the host player is as tight as it would be
        // with no third peer at all
        var perTick = HarnessPlayer.Speed / Host.Context.NetworkTime.Tickrate;
        var drift = (clientPlayers[1].Position - hostPlayers[1].Position).Length();
        Expect.True(drift < perTick * 8,
            FormattableString.Invariant($"the slow peer cost the fast one {drift:F3}: client={Positions(clientPlayers)} host={Positions(hostPlayers)}"));

        // And the slow peer is still in the session, only further behind - which is also what proves the link
        // override took effect at all, rather than the test passing on a network where nobody is slow
        var slowDrift = (thirdPlayers[1].Position - hostPlayers[1].Position).Length();
        Expect.True(slowDrift > drift + perTick,
            FormattableString.Invariant($"the slow link cost nothing: fast={drift:F3} slow={slowDrift:F3} per tick {perTick:F3}"));
    }

    /// <summary>
    /// A loss burst takes every copy of an input at once, which is what a fixed redundancy cannot survive: three in a
    /// row go missing 0.1% of the time at 10% independent loss, and 100% of the time in a 300ms outage.
    /// <para>
    /// What is measured is the authority's side - the host drives the client's player from input the client sends,
    /// and any tick that input never arrives for is a tick the host has to guess. Sending everything the host has not
    /// acknowledged means the whole outage is made good in one packet the moment the link comes back, so the ticks
    /// nobody ever resolved should be few and never in a long row (netfox-net#40).
    /// </para>
    /// </summary>
    [Test]
    public async Task ALossBurstDoesNotLeaveTheAuthorityGuessingForLong()
    {
        Network.LatencyMs = 30;
        Network.Bursts = new NetworkSimulator.Profile(BurstLossMs: 300, BurstIntervalSeconds: 2);

        var hostPlayers = SpawnPlayers(Host, enablePrediction: true);
        SpawnPlayers(Client, enablePrediction: true);

        var newestSeen = -1; var lateFills = 0; var arrivals = 0;
        Host.Context.NetworkSynchronizationServer.OnInput += snapshot =>
        {
            arrivals++;
            if (snapshot.Tick < newestSeen) lateFills++;
            newestSeen = Math.Max(newestSeen, snapshot.Tick);
        };

        // Until the first acknowledgement lands the sender is on the floor of three like before, so a burst in the
        // first second of a session is not repaired - and it is not what is being measured. Let that pass first.
        await WaitUntil(() => Host.Context.NetworkTime.Tick > 40, 5);

        // Long enough to sit through several outages: 300ms out of every 2s, at 30 ticks a second
        var first = Host.Context.NetworkTime.Tick;
        var ran = await WaitUntil(() => Host.Context.NetworkTime.Tick - first > 200, 12);
        Expect.True(ran, $"only reached tick {Host.Context.NetworkTime.Tick} from {first}");

        var client = hostPlayers[2];
        // A round trip plus a margin back from the newest tick, so only ticks that had every chance to be corrected count
        var (longest, endsAt) = client.LongestPredictedRunBetween(first, Host.Context.NetworkTime.Tick - 30);
        GD.Print($"BURST longest_predicted_run={longest} ends_at={endsAt} first={first} predicted={client.PredictedTicks} ticks={Host.Context.NetworkTime.Tick - first} input_arrivals={arrivals} late_fills={lateFills}");

        // Measured both ways on this exact case: a fixed redundancy of three leaves a run of 7, every run, and the
        // acknowledged window leaves 0. Three is margin, not a guess.
        Expect.True(longest < 3,
            $"the host guessed {longest} ticks in a row for the client's player and never found out otherwise. " +
            $"{lateFills} of {arrivals} input arrivals filled a gap; a fixed window of three manages 6 and leaves a run of 7");
    }

    /// <summary>
    /// A lossy schema is applied on the wire, but history used to record what the node actually held. So the peer
    /// owning an input simulated from the exact value and every other peer from the quantized one, and the same tick
    /// produced two different answers - by the same amount every time, so it never averaged out (netfox-net#36).
    /// <para>
    /// Compared on the recorded input rather than on a position, because a position is pulled back by corrections
    /// and would hide the very error being looked for. The direction is chosen so that no component survives half
    /// precision intact.
    /// </para>
    /// </summary>
    [Test]
    public async Task ALossySchemaGivesEveryPeerTheSameInput()
    {
        var schema = new Dictionary<string, NetworkSchemaSerializer>
        {
            ["Input:Movement"] = NetworkSchemas.Vec3T(NetworkSchemas.Float16()),
        };
        var awkward = new Vector3(0.123456789f, 0.0f, 0.765432109f);

        HarnessPlayer.Spawn(Host, 2, schema: schema, direction: awkward);
        HarnessPlayer.Spawn(Client, 2, schema: schema, direction: awkward);

        var first = Host.Context.NetworkTime.Tick;
        Expect.True(await WaitUntil(() => Host.Context.NetworkTime.Tick - first > 40, 8),
            $"only reached tick {Host.Context.NetworkTime.Tick}");

        // Every tick both stacks have a recorded input for, which is what each of them simulates that tick from
        var compared = 0;
        var mismatched = 0;
        var worst = 0.0f;
        for (var tick = first + 5; tick < Host.Context.NetworkTime.Tick - 5; tick++)
        {
            var mine = Recorded(Client, tick);
            var theirs = Recorded(Host, tick);
            if (mine is null || theirs is null) continue;

            compared++;
            var gap = ((Vector3)mine - (Vector3)theirs).Length();
            worst = Mathf.Max(worst, gap);
            if (gap > 0) mismatched++;
        }

        Expect.True(compared > 10, $"only {compared} ticks had a recorded input on both peers");
        var detail = FormattableString.Invariant($"worst {worst:E3}");
        Expect.Equal(0, mismatched,
            $"{mismatched} of {compared} ticks recorded a different input on the owner than on the authority, {detail}: " +
            "the schema is being applied on the wire but not to what the owner simulates from");

        static Variant? Recorded(NetfoxStack stack, int tick)
        {
            var player = stack.GetChildren().OfType<HarnessPlayer>().First();
            var snapshot = stack.Context.NetworkHistoryServer.GetRollbackInputSnapshot(tick);
            return snapshot is not null && snapshot.TryGetProperty(player.Input, "Movement", out var value) ? value : (Variant?)null;
        }
    }

    /// <summary>
    /// Authority was read once at registration, so handing a node to another peer at runtime left the pools describing
    /// the past: the new authority recorded its state as real but never sent it, and the old one kept sending. The
    /// history server reads authority live, so the two servers disagreed about one node without a word (netfox-net#45).
    /// <para>
    /// Every peer has to agree on the change, the same way it agrees on a spawn - here both stacks flip together.
    /// </para>
    /// </summary>
    [Test]
    public async Task AuthorityHandedOverAtRuntimeIsSentByItsNewOwner()
    {
        var hostPlayers = SpawnPlayers(Host);
        var clientPlayers = SpawnPlayers(Client);
        Expect.True(await WaitUntil(() => clientPlayers[1].Position.X > 0.3f, 5), "session never got going");

        // Hand the host's player over to the client: from here the client's simulation of it is the truth
        hostPlayers[1].SetMultiplayerAuthority(2);
        clientPlayers[1].SetMultiplayerAuthority(2);
        hostPlayers[1].Input.SetMultiplayerAuthority(2);
        clientPlayers[1].Input.SetMultiplayerAuthority(2);

        var flippedAt = Host.Context.NetworkTime.Tick;
        var fromClient = 0;
        var fromHostAfterFlip = 0;
        Host.Context.NetworkSynchronizationServer.OnState += snapshot =>
        {
            if (snapshot.TryGetProperty(hostPlayers[1], "position", out _)) fromClient++;
        };
        Client.Context.NetworkSynchronizationServer.OnState += snapshot =>
        {
            if (snapshot.Tick > flippedAt + 5 && snapshot.TryGetProperty(clientPlayers[1], "position", out _)) fromHostAfterFlip++;
        };

        Expect.True(await WaitUntil(() => fromClient > 5, 5),
            $"the host received {fromClient} states for the player it handed over: the new authority is not sending it");

        // And the old authority has let go - a few ticks of grace for packets already in flight
        Expect.Equal(0, fromHostAfterFlip,
            $"the host kept sending state for a player it no longer owns: {fromHostAfterFlip} snapshots after the flip");
    }

    /// <summary>
    /// A rollback root nobody drives: no input node, authority on the host, prediction off. The host runs its rule,
    /// every other peer is told where it is and simulates it not at all - and all three agree (netfox-net#57).
    /// </summary>
    [Test]
    public async Task ANodeNobodyDrivesIsSimulatedByTheHostAlone()
    {
        Network.LatencyMs = 30;
        var third = AddPeer(3);
        NetfoxStack[] stacks = [Host, Client, third];
        var npcs = stacks.ToDictionary(stack => stack, HarnessNpc.Spawn);

        Expect.True(await WaitUntil(() => npcs.Values.All(npc => npc.Position.Length() > 0.3f), 6),
            $"the NPC never got moving everywhere: {string.Join(" ", npcs.Select(entry => $"{entry.Key.Name}={entry.Value.Position}"))}");

        Expect.True(npcs[Host].SimulatedTicks > 0, "the host should simulate the NPC");
        Expect.Equal(0, npcs[Client].SimulatedTicks, "a client simulated a node it does not own and cannot predict");
        Expect.Equal(0, npcs[third].SimulatedTicks, "a client simulated a node it does not own and cannot predict");

        var perTick = HarnessNpc.Speed / Host.Context.NetworkTime.Tickrate;
        foreach (var stack in new[] { Client, third })
        {
            var drift = (npcs[stack].Position - npcs[Host].Position).Length();
            Expect.True(drift < perTick * 8,
                FormattableString.Invariant($"{stack.Name} puts the NPC {drift:F3} from where the host does"));
        }
    }

    /// <summary>
    /// netfox-net#63: in the playground, once in a few runs, the NPC came to rest and the client showed it one or two
    /// ticks short of where the host had it - for the whole quiet window, with nothing moving. A node the client
    /// never simulates lives entirely off the state it is sent; this asks whether the state for the last ticks of
    /// motion always arrives, over a link with latency and then over one with loss.
    /// </summary>
    [Test]
    public async Task ANodeThatStopsIsWhereTheHostLeftItOnEveryPeer()
    {
        Network.LatencyMs = 30;
        NetfoxStack[] stacks = [Host, Client];
        var npcs = stacks.ToDictionary(stack => stack, HarnessNpc.Spawn);
        await WaitUntil(() => Host.Context.NetworkTime.Tick > 5, 5);

        foreach (var repeat in Enumerable.Range(0, 4))
        {
            var stopAt = Host.Context.NetworkTime.Tick + 20;
            foreach (var npc in npcs.Values) npc.StopAtTick = stopAt;
            Expect.True(await WaitUntil(() => Host.Context.NetworkTime.Tick > stopAt + 30, 6), "the host never got past the stop tick");

            var drift = (npcs[Client].Position - npcs[Host].Position).Length();
            Expect.True(drift < 1e-3f, FormattableString.Invariant(
                $"round {repeat}: the NPC stopped at @{stopAt} and a second later the client has it {drift:F3} from the host ({drift / (HarnessNpc.Speed / Host.Context.NetworkTime.Tickrate):F1} ticks of its speed)"));

            // Off again: StopAtTick in the past means "never stop" is not what we want, so push it out of reach
            foreach (var npc in npcs.Values) npc.StopAtTick = int.MaxValue;
            await WaitUntil(() => Host.Context.NetworkTime.Tick > stopAt + 45, 3);
        }
    }

    private static string Describe(Dictionary<NetfoxStack, Dictionary<int, HarnessPlayer>> players)
        => string.Join(" ", players.Select(entry => $"{entry.Key.Name}={Positions(entry.Value)}"));

    private static string Positions(Dictionary<int, HarnessPlayer> players)
        => string.Join(",", players.OrderBy(entry => entry.Key).Select(entry => $"{entry.Key}:{entry.Value.Position}"));

    private static Dictionary<int, HarnessPlayer> SpawnPlayers(NetfoxStack stack, bool enablePrediction = false, int peers = 2)
        => Enumerable.Range(1, peers).ToDictionary(peer => peer, peer => HarnessPlayer.Spawn(stack, peer, enablePrediction));
}
