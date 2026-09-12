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
    private readonly RandomNumberGenerator _rngPacketLoss = new();

    private readonly Dictionary<int, PacketPeerUdp> _clientPeers = new();
    private List<QueueEntry> _clientToServerQueue = new();
    private List<QueueEntry> _serverToClientQueue = new();

    private sealed record QueueEntry(byte[] Packet, ulong QueuedAt, int SourcePort);

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
    {
        Hostname = hostname;
        ServerPort = serverPort;
        LatencyMs = latencyMs;
        PacketLossPercent = packetLossPercent;
        _udpProxyPort = serverPort + 1;

        StartUdpProxy();
        return _udpProxyPort;
    }

    private bool IsProxyRequired() => LatencyMs > 0 || PacketLossPercent > 0.0;

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
        var sendThreshold = currentTime - (ulong)LatencyMs;

        ReadClientToServerPackets(currentTime);
        ProcessClientToServerPackets(sendThreshold);

        if (_clientPeers.Count > 0)
        {
            ReadServerToClientPackets(currentTime);
            ProcessServerToClientQueue(sendThreshold);
        }
    }

    private void LoadProjectSettings()
    {
        Enabled = NetfoxSettings.Instance.AutoconnectEnabled;
        Hostname = NetfoxSettings.Instance.AutoconnectHost;
        ServerPort = NetfoxSettings.Instance.AutoconnectPort;
        UseCompression = NetfoxSettings.Instance.UseCompression;
        LatencyMs = NetfoxSettings.Instance.SimulatedLatencyMs;
        PacketLossPercent = NetfoxSettings.Instance.SimulatedPacketLossChance;
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
            _clientToServerQueue.Add(new QueueEntry(packet, currentTime, fromPort));
        }
    }

    private void RegisterClientIfNew(int port)
    {
        if (_clientPeers.ContainsKey(port)) return;
        var clientPeer = new PacketPeerUdp();
        clientPeer.SetDestAddress(Hostname, ServerPort);
        _clientPeers[port] = clientPeer;
    }

    private void ProcessClientToServerPackets(ulong sendThreshold)
    {
        var keep = new List<QueueEntry>();
        foreach (var entry in _clientToServerQueue)
        {
            if (sendThreshold < entry.QueuedAt)
                keep.Add(entry);
            else if (ShouldSendPacket())
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
                _serverToClientQueue.Add(new QueueEntry(packet, currentTime, clientPort));
            }
        }
    }

    private void ProcessServerToClientQueue(ulong sendThreshold)
    {
        var keep = new List<QueueEntry>();
        foreach (var entry in _serverToClientQueue)
        {
            if (sendThreshold < entry.QueuedAt)
            {
                keep.Add(entry);
            }
            else if (ShouldSendPacket())
            {
                _udpProxyServer!.SetDestAddress(Hostname, entry.SourcePort);
                _udpProxyServer.PutPacket(entry.Packet);
            }
        }
        _serverToClientQueue = keep;
    }

    private bool ShouldSendPacket() => PacketLossPercent <= 0.0 || _rngPacketLoss.Randf() >= PacketLossPercent / 100.0;
}
