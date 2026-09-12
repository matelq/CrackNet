using Godot;

namespace Netfox.Tests;

/// <summary>
/// Two netfox stacks in one SceneTree, talking over a loopback peer. This is the in-process counterpart of
/// examples/e2e: same setup, but assertions instead of a printed line.
/// </summary>
public partial class LoopbackHarnessTests : TestSuite
{
    private LoopbackNetwork _network = null!;
    private NetfoxStack _host = null!;
    private NetfoxStack _client = null!;
    private readonly List<NetfoxStack> _stacks = new();
    private NetfoxSettings _settingsBackup = null!;

    public override async Task BeforeCase()
    {
        // Sync faster than the defaults so a case does not have to wait seconds for the initial clock handshake.
        // The servers read settings when they are constructed, which happens below.
        _settingsBackup = NetfoxSettings.Instance;
        var settings = NetfoxSettings.Load();
        settings.SyncInterval = 0.02;
        settings.SyncSamples = 4;
        settings.SyncAdjustSteps = 2;
        NetfoxSettings.Instance = settings;

        _network = new LoopbackNetwork();
        _host = CreateStack("Host", 1);
        _client = CreateStack("Client", 2);
        _network.Connect();

        await NextFrame();
        await NextFrame();
    }

    public override async Task AfterCase()
    {
        for (var i = _stacks.Count - 1; i >= 0; i--)
            _stacks[i].Teardown();
        _stacks.Clear();

        NetfoxSettings.Instance = _settingsBackup;
        await NextFrame();
    }

    private NetfoxStack CreateStack(string name, int peerId)
    {
        var stack = NetfoxStack.Create(this, name, _network, peerId);
        _stacks.Add(stack);
        return stack;
    }

    [Test]
    public void EachStackHasItsOwnServers()
    {
        Expect.False(ReferenceEquals(_host.Context, _client.Context));
        Expect.False(ReferenceEquals(_host.Context.NetworkTime, _client.Context.NetworkTime));
        Expect.False(ReferenceEquals(_host.Context.NetworkTime, NetworkTime.Instance));
    }

    [Test]
    public void PeersSeeEachOther()
    {
        Expect.True(_host.Api.IsServer(), "host should be the server");
        Expect.False(_client.Api.IsServer(), "client should not be the server");
        Expect.Equal(1, _host.Api.GetUniqueId());
        Expect.Equal(2, _client.Api.GetUniqueId());
        Expect.SequenceEqual([2], _host.Api.GetPeers());
        Expect.SequenceEqual([1], _client.Api.GetPeers());
    }

    [Test]
    public async Task BothStacksStartTicking()
    {
        var ticking = await WaitUntil(() => _host.Context.NetworkTime.Tick > 0 && _client.Context.NetworkTime.Tick > 0);
        Expect.True(ticking, $"host tick {_host.Context.NetworkTime.Tick}, client tick {_client.Context.NetworkTime.Tick}");
    }

    [Test]
    public async Task ClockSyncBringsTheClientOntoTheHostTick()
    {
        var synced = await WaitUntil(() => _client.Context.NetworkTime.IsInitialSyncDone());
        Expect.True(synced, "client never finished its initial sync");

        var hostTick = _host.Context.NetworkTime.Tick;
        var clientTick = _client.Context.NetworkTime.Tick;
        Expect.True(Math.Abs(hostTick - clientTick) <= 4, $"host at {hostTick}, client at {clientTick}");
    }

    [Test]
    public async Task StateFlowsToTheClientAndInputBackToTheHost()
    {
        var hostPlayers = SpawnPlayers(_host);
        var clientPlayers = SpawnPlayers(_client);

        var states = 0;
        var inputs = 0;
        _client.Context.NetworkSynchronizationServer.OnState += _ => states++;
        _host.Context.NetworkSynchronizationServer.OnInput += _ => inputs++;

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
        var hostPlayers = SpawnPlayers(_host);
        var clientPlayers = SpawnPlayers(_client);

        var moved = await WaitUntil(() => clientPlayers[1].Position.X > 0.5f && hostPlayers[1].Position.X > 0.5f, 5);
        Expect.True(moved, $"host {hostPlayers[1].Position}, client {clientPlayers[1].Position}");

        // Both sides agree on where the host player is, up to the ticks the client is behind
        var drift = Math.Abs(hostPlayers[1].Position.X - clientPlayers[1].Position.X);
        var perTick = HarnessPlayer.Speed / _host.Context.NetworkTime.Tickrate;
        Expect.True(drift < perTick * 8, $"drift {drift} over {perTick * 8} (host {hostPlayers[1].Position.X}, client {clientPlayers[1].Position.X})");
    }

    [Test]
    public async Task LateJoiningPeerSyncsAndReceivesState()
    {
        SpawnPlayers(_host);
        SpawnPlayers(_client);
        await WaitUntil(() => _host.Context.NetworkTime.Tick > 5, 5);

        var late = CreateStack("Late", 3);
        _network.ConnectLate(late.Peer);

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
        _network.PacketLoss = 0.3;
        _network.LatencyMs = 20;

        var hostPlayers = SpawnPlayers(_host);
        var clientPlayers = SpawnPlayers(_client);

        var moved = await WaitUntil(() => clientPlayers[1].Position.X > 0.5f && hostPlayers[2].Position.Z > 0.5f, 8);
        Expect.True(moved,
            $"client Player_1={clientPlayers[1].Position} host Player_2={hostPlayers[2].Position} under 30% loss");
    }

    [Test]
    public async Task StateIsNotResentForEveryResimulatedTick()
    {
        // Latency well past the input delay: every client input lands on an already simulated tick, so the host
        // resimulates a range every frame. Sending state for each of those ticks is what upstream #630 is about.
        _network.LatencyMs = 150;

        SpawnPlayers(_host);
        var clientPlayers = SpawnPlayers(_client);

        var states = 0;
        _client.Context.NetworkSynchronizationServer.OnState += _ => states++;

        await WaitUntil(() => clientPlayers[1].Position.X > 0.3f, 6);

        var firstTick = _host.Context.NetworkTime.Tick;
        var firstStates = states;
        await WaitUntil(() => _host.Context.NetworkTime.Tick - firstTick > 30, 6);

        var ticks = _host.Context.NetworkTime.Tick - firstTick;
        var packets = states - firstStates;

        Expect.True(packets <= ticks * 1.5,
            $"{packets} state packets for {ticks} host ticks: state is being re-sent for resimulated ticks");
    }

    [Test]
    public async Task ASecondSessionOnTheSameServersWorks()
    {
        var hostPlayers = SpawnPlayers(_host);
        var clientPlayers = SpawnPlayers(_client);
        Expect.True(await WaitUntil(() => clientPlayers[1].Position.X > 0.3f, 5), "first session never replicated");

        // Run past the history limit, so the tick indexed buffers hold ticks the next session will never reach
        var historyLimit = NetfoxSettings.Instance.RollbackHistoryLimit;
        Expect.True(await WaitUntil(() => _host.Context.NetworkTime.Tick > historyLimit + 20, 8),
            $"first session only reached tick {_host.Context.NetworkTime.Tick}");

        // Leave the lobby: peers go away and the level is torn down, but the servers live on
        _host.Disconnect();
        _client.Disconnect();
        foreach (var player in hostPlayers.Values.Concat(clientPlayers.Values))
        {
            player.GetParent().RemoveChild(player);
            player.Free();
        }
        await NextFrame();
        await NextFrame();

        // Join another lobby with the same stacks
        _network = new LoopbackNetwork();
        _host.Reconnect(_network, 1);
        _client.Reconnect(_network, 2);
        _network.Connect();
        await NextFrame();

        SpawnPlayers(_host);
        var clientPlayers2 = SpawnPlayers(_client);

        Expect.True(await WaitUntil(() => clientPlayers2[1].Position.X > 0.3f, 6),
            $"second session did not replicate: client Player_1={clientPlayers2[1].Position}");
    }

    [Test]
    public async Task ASecondSessionWithTheSameNodesWorks()
    {
        var hostPlayers = SpawnPlayers(_host);
        var clientPlayers = SpawnPlayers(_client);
        Expect.True(await WaitUntil(() => clientPlayers[1].Position.X > 0.3f, 5), "first session never replicated");

        // Stand in for a long session: history ends up far above the tick the next session will start at
        _host.Context.NetworkTime.SetTick(2000);
        _client.Context.NetworkTime.SetTick(2000);
        Expect.True(await WaitUntil(() => _host.Context.NetworkTime.Tick > 2010, 8),
            $"first session only reached tick {_host.Context.NetworkTime.Tick}");

        // Leave the lobby, but keep the scene: the nodes, their synchronizers and their history all survive
        _host.Disconnect();
        _client.Disconnect();
        await NextFrame();
        await NextFrame();

        var restartFrom = clientPlayers[1].Position.X;

        _network = new LoopbackNetwork();
        _host.Reconnect(_network, 1);
        _client.Reconnect(_network, 2);
        _network.Connect();

        var states = 0;
        _client.Context.NetworkSynchronizationServer.OnState += _ => states++;

        Expect.True(await WaitUntil(() => clientPlayers[1].Position.X > restartFrom + 0.3f, 8),
            $"second session did not replicate with the same nodes: client Player_1={clientPlayers[1].Position}, " +
            $"host Player_1={hostPlayers[1].Position}, host tick {_host.Context.NetworkTime.Tick}, states received {states}, " +
            $"host owns {_host.Context.NetworkSynchronizationServer.OwnedRollbackStateProperties.Subjects.Count} subjects");
    }

    private static Dictionary<int, HarnessPlayer> SpawnPlayers(NetfoxStack stack) => new()
    {
        [1] = HarnessPlayer.Spawn(stack, 1),
        [2] = HarnessPlayer.Spawn(stack, 2),
    };
}
