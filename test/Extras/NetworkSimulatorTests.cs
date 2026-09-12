using Godot;
using Netfox.Extras;

namespace Netfox.Tests;

/// <summary>
/// #13: the autoconnect flow has to work with peers other than ENet, so a Steam or loopback peer can use it. The cases
/// drive <c>Connect</c> directly, since <c>_Ready</c> is guarded to the editor and refuses to run on CI.
/// </summary>
public partial class NetworkSimulatorTests : TestSuite
{
    private Func<NetworkSimulator, MultiplayerPeer?> _hostBackup = null!;
    private Func<NetworkSimulator, MultiplayerPeer?> _joinBackup = null!;
    private LoopbackNetwork _network = null!;
    private Node _branch = null!;

    public override async Task BeforeCase()
    {
        _hostBackup = NetworkSimulator.HostPeerFactory;
        _joinBackup = NetworkSimulator.JoinPeerFactory;
        _network = new LoopbackNetwork();

        // The simulator assigns a peer to its MultiplayerAPI, so give it one of its own rather than the tree's
        _branch = new Node { Name = "Simulated" };
        GetTree().SetMultiplayer(MultiplayerApi.CreateDefaultInterface(), new NodePath($"{GetPath()}/Simulated"));
        await Mount(_branch);
    }

    public override async Task AfterCase()
    {
        NetworkSimulator.HostPeerFactory = _hostBackup;
        NetworkSimulator.JoinPeerFactory = _joinBackup;

        var path = _branch.GetPath();
        _branch.Multiplayer.MultiplayerPeer?.Close();
        _branch.Multiplayer.MultiplayerPeer = null;

        FreeChildren();
        GetTree().SetMultiplayer(null, path);
        await NextFrame();
    }

    private async Task<NetworkSimulator> Simulator(string name)
    {
        var simulator = new NetworkSimulator { Name = name };
        _branch.AddChild(simulator);
        if (!simulator.IsNodeReady()) await ToSignal(simulator, Node.SignalName.Ready);
        return simulator;
    }

    [Test]
    public async Task HostsWithTheInjectedPeer()
    {
        var hosted = 0;
        var joined = 0;
        NetworkSimulator.HostPeerFactory = _ => _network.CreatePeer(1);
        NetworkSimulator.JoinPeerFactory = _ => throw new InvalidOperationException("should not have tried to join");

        var simulator = await Simulator("Hosting Simulator");
        simulator.ServerCreated += () => hosted++;
        simulator.ClientConnected += () => joined++;
        simulator.Connect();

        Expect.Equal(1, hosted);
        Expect.Equal(0, joined);
        Expect.True(simulator.Peer is LoopbackMultiplayerPeer, $"expected the injected peer, got {simulator.Peer}");
        Expect.True(ReferenceEquals(simulator.Peer, simulator.Multiplayer.MultiplayerPeer), "the peer should be assigned to the API");
    }

    [Test]
    public async Task JoinsWithTheInjectedPeerWhenHostingIsTaken()
    {
        var hosted = 0;
        var joined = 0;
        NetworkSimulator.HostPeerFactory = _ => null;
        NetworkSimulator.JoinPeerFactory = _ => _network.CreatePeer(2);

        var simulator = await Simulator("Joining Simulator");
        simulator.ServerCreated += () => hosted++;
        simulator.ClientConnected += () => joined++;
        simulator.Connect();

        Expect.Equal(0, hosted);
        Expect.Equal(1, joined);
        Expect.Equal(2, simulator.Peer!.GetUniqueId());
    }

    [Test]
    public async Task SurvivesAFactoryThatCanNeitherHostNorJoin()
    {
        NetworkSimulator.HostPeerFactory = _ => null;
        NetworkSimulator.JoinPeerFactory = _ => null;

        var simulator = await Simulator("Failing Simulator");
        simulator.Connect();

        Expect.Null(simulator.Peer);
        // A MultiplayerAPI without a peer reports the offline one, so "nothing was assigned" looks like this
        Expect.True(simulator.Multiplayer.MultiplayerPeer is OfflineMultiplayerPeer,
            $"nothing should have been assigned, got {simulator.Multiplayer.MultiplayerPeer}");
    }

    /// <summary>The proxy is a UDP forwarder, so a non-ENet peer must not be sent through its port.</summary>
    [Test]
    public async Task ConnectPortSkipsTheProxyWhenThereIsNothingToSimulate()
    {
        var simulator = await Simulator("Direct Simulator");
        NetworkSimulator.HostPeerFactory = _ => _network.CreatePeer(1);
        simulator.Connect();

        Expect.Equal(simulator.ServerPort, simulator.ConnectPort);
    }
}
