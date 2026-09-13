using System.Threading;
using Godot;
using Netfox.Core.Logging;
using Netfox.Internal;

namespace Netfox.Extras;

/// <summary>
/// Editor convenience: the first launched instance hosts, later ones join, optionally through a UDP proxy that
/// injects latency and packet loss. ENet only. Port of netfox.extras/network-simulator.gd.
/// </summary>
public partial class NetworkSimulator : Node
{
    private static readonly NetfoxLogger Logger = NetfoxLogger.ForExtras("NetworkSimulator");

    public event Action? ServerCreated;
    public event Action? ClientConnected;

    public bool Enabled { get; private set; }
    public string Hostname { get; private set; } = "127.0.0.1";
    public int ServerPort { get; private set; } = 9999;
    public bool UseCompression { get; private set; }
    public int LatencyMs { get; private set; }
    public double PacketLossPercent { get; private set; }

    /// <summary>
    /// What the proxy does to a packet, beyond the constant delay and even loss upstream models. Reworked only.
    /// <para>
    /// A real link does neither of those. Independent loss at 10% takes three packets in a row one time in a
    /// thousand, so an input redundancy of three covers essentially every gap it makes; a burst takes all three at
    /// once and leaves the authority predicting. And a delay that never varies never stresses clock synchronization
    /// or arrival order, because it is perfectly predictable.
    /// </para>
    /// </summary>
    /// <param name="LatencyMs">One-way delay before jitter, in milliseconds.</param>
    /// <param name="PacketLossPercent">Steady, independent per-packet loss, 0 to 100.</param>
    /// <param name="JitterMs">How much a packet's delay may exceed <paramref name="LatencyMs"/>. One-sided: nothing
    /// arrives faster than the link allows. Packets can overtake each other, which is the point.</param>
    /// <param name="JitterPeriodSeconds">Zero for random jitter, uniform over the spread. Above zero it oscillates
    /// smoothly between nothing and the full spread over this many seconds, which is a different beast: every packet
    /// in a stretch is late together, so a jitter buffer that copes with noise can still starve.</param>
    /// <param name="BurstLossMs">How long a burst drops everything, in both directions.</param>
    /// <param name="BurstIntervalSeconds">How often a burst starts. Zero for no bursts.</param>
    public sealed record Profile(
        int LatencyMs = 0,
        double PacketLossPercent = 0,
        int JitterMs = 0,
        double JitterPeriodSeconds = 0,
        int BurstLossMs = 0,
        double BurstIntervalSeconds = 0)
    {
        /// <summary>
        /// What mas-bandwidth recommends playtesting under: "at least 50ms round trip latency, several frames worth
        /// of jitter, and a mix of steady packet loss at 1% combined with bursts of packet loss at least once
        /// per-minute" (https://mas-bandwidth.com/what-is-lag/).
        /// <para>
        /// 25ms each way is the 50ms round trip. 20ms of jitter is several frames at 60fps. The burst is 200ms, which
        /// at a 30Hz tickrate is six ticks with nothing in them - past what an input redundancy of three can cover,
        /// which is the interesting part. It fires every five seconds rather than every sixty: the article is
        /// describing a playtest, and a check that runs for fifteen seconds would meet a once-a-minute burst one run
        /// in four, which is no better than not having one.
        /// </para>
        /// </summary>
        public static readonly Profile Realistic =
            new(LatencyMs: 25, PacketLossPercent: 1, JitterMs: 20, BurstLossMs: 200, BurstIntervalSeconds: 5);

        /// <summary>
        /// Worse than anyone should have to play on, which is what a check wants. <see cref="Realistic"/> is a floor
        /// to playtest above, and it is too kind to catch things: the netfox-net#35 desync does not reproduce under
        /// it at all, because 25ms each way arrives well inside the input delay and nothing is ever missing.
        /// <para>
        /// 120ms each way is what puts every input behind the tick it was meant for, which is the condition that
        /// makes prediction load-bearing. The same bug fails on the first run here, and reported a disagreement of
        /// 637m.
        /// </para>
        /// </summary>
        public static readonly Profile Hostile =
            new(LatencyMs: 120, PacketLossPercent: 10, JitterMs: 40, BurstLossMs: 300, BurstIntervalSeconds: 3);
    }

    /// <summary>Packets the proxy passed through, and the two ways it did not. Zero bursts means no burst fired.</summary>
    public (long Forwarded, long Dropped, long BurstDropped) ProxyCounts
        => (Interlocked.Read(ref _forwarded), Interlocked.Read(ref _dropped), Interlocked.Read(ref _burstDropped));

    private long _forwarded;
    private long _dropped;
    private long _burstDropped;

    /// <summary>Jitter and burst loss on top of <see cref="LatencyMs"/> and <see cref="PacketLossPercent"/>.</summary>
    public Profile Conditions { get; private set; } = new();

    /// <summary>Port clients connect to when the latency and loss proxy is in the way; otherwise <see cref="ServerPort"/>.</summary>
    public int ProxyPort => _udpProxyPort;

    /// <summary>The port to actually dial: the proxy's when it is running, the server's otherwise.</summary>
    public int ConnectPort => IsProxyRequired() ? _udpProxyPort : ServerPort;

    /// <summary>
    /// Creates the peer to host with, or null when this instance could not take the host role - which is what makes
    /// the next one join instead.
    /// <para>
    /// Upstream hardcodes <see cref="ENetMultiplayerPeer"/> (network-simulator.gd:70), so autoconnect is unusable with
    /// a Steam or loopback peer. Replace these before the simulator enters the tree to autoconnect over any transport.
    /// The UDP proxy only applies to ENet and is skipped for anything else, since it forwards real UDP packets.
    /// </para>
    /// </summary>
    public static Func<NetworkSimulator, MultiplayerPeer?> HostPeerFactory { get; set; } = CreateENetServer;

    /// <summary>Creates the peer to join with. See <see cref="HostPeerFactory"/>.</summary>
    public static Func<NetworkSimulator, MultiplayerPeer?> JoinPeerFactory { get; set; } = CreateENetClient;

    /// <summary>The peer the last autoconnect produced, or null if it never got one.</summary>
    public MultiplayerPeer? Peer { get; private set; }

    private Thread? _proxyThread;
    private volatile bool _proxyLoopEnabled = true;
    private PacketPeerUdp? _udpProxyServer;
    private int _udpProxyPort;
    private readonly RandomNumberGenerator _rng = new();

    private readonly Dictionary<int, PacketPeerUdp> _clientPeers = new();
    private List<QueueEntry> _clientToServerQueue = new();
    private List<QueueEntry> _serverToClientQueue = new();

    /// <summary>A packet held back until <paramref name="SendAt"/>. Each gets its own, so jitter can reorder them.</summary>
    private sealed record QueueEntry(byte[] Packet, ulong SendAt, int SourcePort);

    public override async void _Ready()
    {
        if (!OS.HasFeature("editor"))
        {
            Logger.Debug("Running outside editor, disabling");
            return;
        }

        LoadProjectSettings();
        if (!Enabled)
        {
            Logger.Debug("Feature disabled");
            return;
        }

        foreach (var envVar in new[] { "CI", "NETFOX_CI", "NETFOX_NO_AUTOCONNECT" })
        {
            if (OS.GetEnvironment(envVar).Length == 0) continue;
            Logger.Debug("Environment variable {0} set, disabling", envVar);
            return;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (!IsInsideTree()) return;

        Connect();
    }

    /// <summary>
    /// The autoconnect itself, without the editor and environment guards around it: host if nothing else has, join if
    /// something has. Assigns the resulting peer to the multiplayer API.
    /// </summary>
    internal void Connect()
    {
        _udpProxyPort = ServerPort + 1;

        Peer = HostPeerFactory(this);
        if (Peer is not null)
        {
            if (IsProxyRequired()) StartUdpProxy();
            ServerCreated?.Invoke();
            Logger.Info("Server started on port {0}", ServerPort);
        }
        else
        {
            Peer = JoinPeerFactory(this);
            if (Peer is null)
            {
                Logger.Error("Autoconnect could neither host nor join");
                return;
            }

            ClientConnected?.Invoke();
            Logger.Info("Client connected to {0}:{1}", Hostname, ConnectPort);
        }

        // Compression is an ENet feature; other transports bring their own, or none
        if (UseCompression && Peer is ENetMultiplayerPeer enet)
            enet.Host.Compress(ENetConnection.CompressionMode.RangeCoder);

        Multiplayer.MultiplayerPeer = Peer;
    }

    private static MultiplayerPeer? CreateENetServer(NetworkSimulator simulator)
    {
        var peer = new ENetMultiplayerPeer();
        var status = peer.CreateServer(simulator.ServerPort);
        if (status == Error.Ok) return peer;

        // Anything but "the port is taken" is worth saying out loud before we go and join
        if (status != Error.CantCreate)
            Logger.Error("Hosting failed with error - {0}", status);
        return null;
    }

    private static MultiplayerPeer? CreateENetClient(NetworkSimulator simulator)
    {
        var peer = new ENetMultiplayerPeer();
        var status = peer.CreateClient(simulator.Hostname, simulator.ConnectPort);
        if (status == Error.Ok) return peer;

        Logger.Error("Joining failed with error - {0}", status);
        return null;
    }

    public override void _ExitTree()
    {
        if (_proxyThread is null) return;
        _proxyLoopEnabled = false;
        _proxyThread.Join();
    }

    /// <summary>
    /// Starts only the latency and loss proxy, without the autoconnect flow that is limited to the editor, and returns
    /// the port clients should connect to. Upstream has no such entry point: its proxy is reachable only through
    /// autoconnect, which is why it was never covered by a headless run.
    /// </summary>
    public int StartProxy(int serverPort, int latencyMs, double packetLossPercent, string hostname = "127.0.0.1")
        => StartProxy(serverPort, new Profile(latencyMs, packetLossPercent), hostname);

    /// <summary>
    /// Starts the proxy with jitter and burst loss as well as the constant delay and even loss.
    /// <see cref="Profile.Realistic"/> is the one worth running against.
    /// </summary>
    public int StartProxy(int serverPort, Profile profile, string hostname = "127.0.0.1")
    {
        Hostname = hostname;
        ServerPort = serverPort;
        Conditions = profile;
        LatencyMs = profile.LatencyMs;
        PacketLossPercent = profile.PacketLossPercent;
        _udpProxyPort = serverPort + 1;

        StartUdpProxy();
        return _udpProxyPort;
    }

    private bool IsProxyRequired()
        => LatencyMs > 0 || PacketLossPercent > 0.0 || Conditions.JitterMs > 0 || Conditions.BurstLossMs > 0;

    // Listens on the proxy port, forwards to the server port with delay and loss, on its own thread
    private void StartUdpProxy()
    {
        _udpProxyServer = new PacketPeerUdp();
        var bindStatus = _udpProxyServer.Bind(_udpProxyPort, Hostname);
        if (bindStatus != Error.Ok)
        {
            Logger.Error("Failed to bind UDP proxy port: {0}", bindStatus);
            return;
        }

        _proxyThread = new Thread(ProcessLoop) { IsBackground = true, Name = "netfox UDP proxy" };
        _proxyThread.Start();
    }

    private void ProcessLoop()
    {
        while (_proxyLoopEnabled)
        {
            ProcessPackets();
            Thread.Sleep(1);
        }
    }

    private void ProcessPackets()
    {
        var currentTime = Time.GetTicksMsec();

        ReadClientToServerPackets(currentTime);
        ProcessClientToServerPackets(currentTime);

        if (_clientPeers.Count > 0)
        {
            ReadServerToClientPackets(currentTime);
            ProcessServerToClientQueue(currentTime);
        }
    }

    /// <summary>
    /// When a packet entering the link now should come out the other end, or null if it never does. Both are decided
    /// here rather than on the way out: a packet lost on the wire was lost when it was sent, and a delay that is
    /// rolled per packet is what lets a later one arrive first.
    /// </summary>
    private ulong? ScheduleOrDrop(ulong now)
    {
        if (InLossBurst(Conditions, now))
        {
            Interlocked.Increment(ref _burstDropped);
            return null;
        }

        if (PacketLossPercent > 0.0 && _rng.Randf() < PacketLossPercent / 100.0)
        {
            Interlocked.Increment(ref _dropped);
            return null;
        }

        Interlocked.Increment(ref _forwarded);
        return now + (ulong)(LatencyMs + Jitter(now));
    }

    private int Jitter(ulong now)
    {
        var spread = Conditions.JitterMs;
        if (spread <= 0) return 0;
        return Conditions.JitterPeriodSeconds <= 0 ? _rng.RandiRange(0, spread) : OscillatingJitter(Conditions, now);
    }

    /// <summary>A raised cosine over the period, so the delay drifts up and back down rather than jumping.</summary>
    internal static int OscillatingJitter(Profile profile, ulong now)
    {
        var phase = now / 1000.0 % profile.JitterPeriodSeconds / profile.JitterPeriodSeconds;
        return (int)(profile.JitterMs * (1 - Math.Cos(phase * Math.Tau)) / 2);
    }

    /// <summary>
    /// Whether the link is in one of its outages. Derived from the clock rather than scheduled, so it needs no state
    /// and both directions go out together - which is what an outage is, as against loss on one path.
    /// </summary>
    internal static bool InLossBurst(Profile profile, ulong now)
    {
        if (profile.BurstLossMs <= 0 || profile.BurstIntervalSeconds <= 0) return false;
        return now % (ulong)(profile.BurstIntervalSeconds * 1000) < (ulong)profile.BurstLossMs;
    }

    private void LoadProjectSettings()
    {
        Enabled = NetfoxSettings.Instance.AutoconnectEnabled;
        Hostname = NetfoxSettings.Instance.AutoconnectHost;
        ServerPort = NetfoxSettings.Instance.AutoconnectPort;
        UseCompression = NetfoxSettings.Instance.UseCompression;
        LatencyMs = NetfoxSettings.Instance.SimulatedLatencyMs;
        PacketLossPercent = NetfoxSettings.Instance.SimulatedPacketLossChance;

        // Jitter and bursts have no project settings of their own: the autoconnect flow is an editor convenience, and
        // the checks that want them go through StartProxy. Add settings when someone wants them in the editor.
        Conditions = new Profile(LatencyMs, PacketLossPercent);
    }

    private void ReadClientToServerPackets(ulong currentTime)
    {
        while (_udpProxyServer!.GetAvailablePacketCount() > 0)
        {
            var packet = _udpProxyServer.GetPacket();
            var error = _udpProxyServer.GetPacketError();
            if (error != Error.Ok)
            {
                Logger.Error("UDP proxy incoming packet error: {0}", error);
                continue;
            }

            var fromPort = _udpProxyServer.GetPacketPort();
            RegisterClientIfNew(fromPort);

            if (ScheduleOrDrop(currentTime) is { } sendAt)
                _clientToServerQueue.Add(new QueueEntry(packet, sendAt, fromPort));
        }
    }

    private void RegisterClientIfNew(int port)
    {
        if (_clientPeers.ContainsKey(port)) return;
        var clientPeer = new PacketPeerUdp();
        clientPeer.SetDestAddress(Hostname, ServerPort);
        _clientPeers[port] = clientPeer;
    }

    private void ProcessClientToServerPackets(ulong now)
    {
        var keep = new List<QueueEntry>();
        foreach (var entry in _clientToServerQueue)
        {
            if (entry.SendAt > now)
                keep.Add(entry);
            else
                _clientPeers[entry.SourcePort].PutPacket(entry.Packet);
        }
        _clientToServerQueue = keep;
    }

    private void ReadServerToClientPackets(ulong currentTime)
    {
        foreach (var (clientPort, clientPeer) in _clientPeers)
        {
            while (clientPeer.GetAvailablePacketCount() > 0)
            {
                var packet = clientPeer.GetPacket();
                var error = clientPeer.GetPacketError();
                if (error != Error.Ok)
                {
                    Logger.Error("UDP proxy server-to-client packet error from port {0} : {1}", clientPort, error);
                    continue;
                }
                if (ScheduleOrDrop(currentTime) is { } sendAt)
                    _serverToClientQueue.Add(new QueueEntry(packet, sendAt, clientPort));
            }
        }
    }

    private void ProcessServerToClientQueue(ulong now)
    {
        var keep = new List<QueueEntry>();
        foreach (var entry in _serverToClientQueue)
        {
            if (entry.SendAt > now)
            {
                keep.Add(entry);
            }
            else
            {
                _udpProxyServer!.SetDestAddress(Hostname, entry.SourcePort);
                _udpProxyServer.PutPacket(entry.Packet);
            }
        }
        _serverToClientQueue = keep;
    }
}
