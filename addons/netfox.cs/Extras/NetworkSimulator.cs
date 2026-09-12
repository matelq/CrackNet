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

    private readonly ENetMultiplayerPeer _enetPeer = new();

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
        _udpProxyPort = ServerPort + 1;

        var status = TryAndHost();
        if (status == Error.CantCreate)
            TryAndJoin();
        else if (status != Error.Ok)
            Logger.Error("Autoconnect failed with error - {0}", status);

        if (UseCompression)
            _enetPeer.Host.Compress(ENetConnection.CompressionMode.RangeCoder);

        Multiplayer.MultiplayerPeer = _enetPeer;
    }

    public override void _ExitTree()
    {
        if (_proxyThread is null) return;
        _proxyLoopEnabled = false;
        _proxyThread.Join();
    }

    private bool IsProxyRequired() => LatencyMs > 0 || PacketLossPercent > 0.0;

    private Error TryAndHost()
    {
        var status = _enetPeer.CreateServer(ServerPort);
        if (status == Error.Ok)
        {
            if (IsProxyRequired()) StartUdpProxy();
            ServerCreated?.Invoke();
            Logger.Info("Server started on port {0}", ServerPort);
        }
        return status;
    }

    private Error TryAndJoin()
    {
        var connectPort = IsProxyRequired() ? _udpProxyPort : ServerPort;
        var status = _enetPeer.CreateClient(Hostname, connectPort);
        if (status == Error.Ok)
        {
            ClientConnected?.Invoke();
            Logger.Info("Client connected to {0}:{1}", Hostname, connectPort);
        }
        return status;
    }

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
