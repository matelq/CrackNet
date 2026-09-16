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

    /// <summary>
    /// The editor setting is a chance, 0 to 1; the proxy counts in percent. Upstream reads one into the other as-is,
    /// so "0.3" in the editor dropped 0.3% of packets, and a four-window playtest believed to run under 30% loss ran
    /// under almost none. The two must meet in the middle exactly once.
    /// </summary>
    [Test]
    public async Task TheLossSettingIsAChanceAndTheProxyCountsPercent()
    {
        var backup = NetfoxSettings.Instance;
        var settings = NetfoxSettings.Load();
        settings.SimulatedPacketLossChance = 0.3;
        NetfoxSettings.Instance = settings;
        try
        {
            var simulator = await Simulator("Lossy");
            Expect.True(Math.Abs(simulator.PacketLossPercent - 30.0) < 1e-9, $"a chance of 0.3 became {simulator.PacketLossPercent}%");
        }
        finally
        {
            NetfoxSettings.Instance = backup;
        }
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

    /// <summary>
    /// A burst that never fires reads exactly like a clean link, so the arithmetic that decides when one is on is
    /// worth pinning: seconds against milliseconds, and a modulo on an unsigned clock.
    /// </summary>
    [Test]
    public void BurstsFireForTheirDurationAndThenStop()
    {
        var profile = new NetworkSimulator.Profile(BurstLossMs: 300, BurstIntervalSeconds: 5);

        Expect.True(NetworkSimulator.InLossBurst(profile, 0), "a burst should be on at the start of an interval");
        Expect.True(NetworkSimulator.InLossBurst(profile, 299), "299ms into a 300ms burst");
        Expect.False(NetworkSimulator.InLossBurst(profile, 300), "300ms is one past the end");
        Expect.False(NetworkSimulator.InLossBurst(profile, 4999), "the quiet stretch before the next one");
        Expect.True(NetworkSimulator.InLossBurst(profile, 5000), "the next interval starts another burst");
        Expect.True(NetworkSimulator.InLossBurst(profile, 100_000_100), "and it keeps working far from zero");

        // Which is 6% of the time, and the fraction is the point: it has to beat an input redundancy of three
        var on = Enumerable.Range(0, 10_000).Count(ms => NetworkSimulator.InLossBurst(profile, (ulong)ms));
        Expect.Equal(600, on);

        Expect.False(NetworkSimulator.InLossBurst(new NetworkSimulator.Profile(BurstLossMs: 300), 0),
            "no interval means no bursts");
        Expect.False(NetworkSimulator.InLossBurst(new NetworkSimulator.Profile(BurstIntervalSeconds: 5), 0),
            "no duration means no bursts");
    }

    /// <summary>Oscillating jitter has to reach both ends of its spread, or it is just a constant delay again.</summary>
    [Test]
    public void OscillatingJitterSweepsTheWholeSpread()
    {
        var profile = new NetworkSimulator.Profile(JitterMs: 100, JitterPeriodSeconds: 10);

        Expect.Equal(0, NetworkSimulator.OscillatingJitter(profile, 0));
        Expect.Equal(100, NetworkSimulator.OscillatingJitter(profile, 5000));
        Expect.Equal(0, NetworkSimulator.OscillatingJitter(profile, 10_000));

        // And never leaves it, which is what keeps a packet from arriving before the link allows
        for (var ms = 0; ms < 10_000; ms += 137)
        {
            var jitter = NetworkSimulator.OscillatingJitter(profile, (ulong)ms);
            Expect.True(jitter is >= 0 and <= 100, $"jitter {jitter} at {ms}ms is outside 0..100");
        }
    }

    /// <summary>The named profiles are what checks ask for by name, so their shape is part of the contract.</summary>
    [Test]
    public void TheNamedProfilesGetWorseInOrder()
    {
        string[] names = ["clear", "casual", "realistic", "bad", "hostile"];
        var profiles = names.Select(name => NetworkSimulator.Profile.Named(name)!).ToList();
        Expect.True(ReferenceEquals(NetworkSimulator.Profile.Bad, NetworkSimulator.Profile.Default));
        Expect.Null(NetworkSimulator.Profile.Named("nope"));
        Expect.True(NetworkSimulator.Profile.Clear == new NetworkSimulator.Profile(), "clear is no conditions at all");

        for (var i = 1; i < profiles.Count; i++)
        {
            var (better, worse) = (profiles[i - 1], profiles[i]);
            Expect.True(worse.LatencyMs > better.LatencyMs && worse.JitterMs > better.JitterMs
                        && worse.PacketLossPercent > better.PacketLossPercent && worse.BurstLossMs > better.BurstLossMs,
                $"{names[i]} is not worse than {names[i - 1]}");
            Expect.True(NetworkSimulator.InLossBurst(worse, 0), $"{names[i]} never bursts");
        }
    }
}
