using CrackNet.Core.Logging;
using CrackNet.Internal;
using Godot;

namespace CrackNet.Extras;

/// <summary>
/// Editor convenience: the first launched instance hosts and later ones join. Link conditions are applied in process
/// by <see cref="SimulatedMultiplayerPeer"/>, once as each packet leaves for a remote peer.
/// <para>
/// A game with its own transport (a mesh, Steam) subscribes to <see cref="RoleElected"/>: the simulator then only
/// elects the role and builds no peer, and the game connects with <see cref="Conditions"/> itself.
/// </para>
/// </summary>
public partial class NetworkSimulator : Node
{
    private static readonly CrackNetLogger Logger = CrackNetLogger.ForExtras("NetworkSimulator");

    public event Action? ServerCreated;
    public event Action? ClientConnected;

    public bool Enabled { get; private set; }
    public string Hostname { get; private set; } = "127.0.0.1";
    public int ServerPort { get; private set; } = 9999;
    public bool UseCompression { get; private set; }
    public int LatencyMs { get; private set; }
    public double PacketLossPercent { get; private set; }

    /// <summary>Latency, loss, jitter and outage conditions for one directed send over a peer link.</summary>
    /// <param name="LatencyMs">One-way delay before jitter, in milliseconds.</param>
    /// <param name="PacketLossPercent">Steady, independent per-packet loss, 0 to 100.</param>
    /// <param name="JitterMs">How much a packet's delay may exceed <paramref name="LatencyMs"/>.</param>
    /// <param name="JitterPeriodSeconds">Zero for random jitter; above zero, a raised-cosine oscillation period.</param>
    /// <param name="BurstLossMs">How long each unreliable-packet outage lasts.</param>
    /// <param name="BurstIntervalSeconds">How often an outage begins; zero for none.</param>
    public sealed record Profile(
        int LatencyMs = 0,
        double PacketLossPercent = 0,
        int JitterMs = 0,
        double JitterPeriodSeconds = 0,
        int BurstLossMs = 0,
        double BurstIntervalSeconds = 0)
    {
        public static readonly Profile Clear = new();
        public static readonly Profile Casual =
            new(LatencyMs: 25, PacketLossPercent: 1, JitterMs: 20, BurstLossMs: 50, BurstIntervalSeconds: 10);
        public static readonly Profile Realistic =
            new(LatencyMs: 60, PacketLossPercent: 3, JitterMs: 50, BurstLossMs: 100, BurstIntervalSeconds: 10);
        public static readonly Profile Bad =
            new(LatencyMs: 150, PacketLossPercent: 5, JitterMs: 100, BurstLossMs: 200, BurstIntervalSeconds: 5);
        public static readonly Profile Hostile =
            new(LatencyMs: 250, PacketLossPercent: 15, JitterMs: 150, BurstLossMs: 300, BurstIntervalSeconds: 3);
        public static Profile Default => Bad;

        public static Profile? Named(string name) => name.ToLowerInvariant() switch
        {
            "clear" => Clear,
            "casual" => Casual,
            "realistic" => Realistic,
            "bad" => Bad,
            "hostile" => Hostile,
            _ => null,
        };
    }

    /// <summary>Conditions applied by the in-process wrapper.</summary>
    public Profile Conditions { get; private set; } = new();

    /// <summary>
    /// Raised with true on the instance elected host and false on the others, instead of connecting: subscribing
    /// means the game connects itself. The election holds a UDP port, <see cref="ElectionPort"/>, for the session.
    /// </summary>
    public event Action<bool>? RoleElected;

    /// <summary>The port whose owner is the host: one below <see cref="ServerPort"/>, clear of the game's own ports.</summary>
    public int ElectionPort => ServerPort - 1;

    private PacketPeerUdp? _election;

    /// <summary>The peer produced by autoconnect, wrapped with this instance's link conditions.</summary>
    public MultiplayerPeer? Peer { get; private set; }

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

        foreach (var envVar in new[] { "CI", "CRACKNET_CI", "CRACKNET_NO_AUTOCONNECT" })
        {
            if (OS.GetEnvironment(envVar).Length == 0) continue;
            Logger.Debug("Environment variable {0} set, disabling", envVar);
            return;
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (IsInsideTree()) Connect();
    }

    /// <summary>
    /// Elects the role for a game that connects itself, or hosts if the configured port is free, otherwise joins, then
    /// installs the simulated peer.
    /// </summary>
    internal void Connect()
    {
        if (RoleElected is not null)
        {
            _election = new PacketPeerUdp();
            RoleElected(_election.Bind(ElectionPort) == Error.Ok);
            return;
        }

        var raw = CreateENetServer(this);
        var hosted = raw is not null;
        if (raw is null) raw = CreateENetClient(this);
        if (raw is null)
        {
            Logger.Error("Autoconnect could neither host nor join");
            return;
        }

        if (UseCompression && raw is ENetMultiplayerPeer enet)
            enet.Host.Compress(ENetConnection.CompressionMode.RangeCoder);

        Peer = Conditions == Profile.Clear ? raw : new SimulatedMultiplayerPeer(raw, Conditions);
        Multiplayer.MultiplayerPeer = Peer;
        if (hosted)
        {
            ServerCreated?.Invoke();
            Logger.Info("Server started on port {0}", ServerPort);
        }
        else
        {
            ClientConnected?.Invoke();
            Logger.Info("Client connected to {0}:{1}", Hostname, ServerPort);
        }
    }

    private static MultiplayerPeer? CreateENetServer(NetworkSimulator simulator)
    {
        var peer = new ENetMultiplayerPeer();
        var status = peer.CreateServer(simulator.ServerPort);
        if (status == Error.Ok) return peer;
        if (status != Error.CantCreate) Logger.Error("Hosting failed with error - {0}", status);
        return null;
    }

    private static MultiplayerPeer? CreateENetClient(NetworkSimulator simulator)
    {
        var peer = new ENetMultiplayerPeer();
        var status = peer.CreateClient(simulator.Hostname, simulator.ServerPort);
        if (status == Error.Ok) return peer;
        Logger.Error("Joining failed with error - {0}", status);
        return null;
    }

    public override void _ExitTree() => _election?.Close();

    /// <summary>A raised cosine over the period, so delay drifts instead of jumping.</summary>
    internal static int OscillatingJitter(Profile profile, ulong now)
    {
        var phase = now / 1000.0 % profile.JitterPeriodSeconds / profile.JitterPeriodSeconds;
        return (int)(profile.JitterMs * (1 - Math.Cos(phase * Math.Tau)) / 2);
    }

    /// <summary>Whether an unreliable packet sent now falls inside a periodic link outage.</summary>
    internal static bool InLossBurst(Profile profile, ulong now)
    {
        if (profile.BurstLossMs <= 0 || profile.BurstIntervalSeconds <= 0) return false;
        return now % (ulong)(profile.BurstIntervalSeconds * 1000) < (ulong)profile.BurstLossMs;
    }

    private void LoadProjectSettings()
    {
        Enabled = CrackNetSettings.Instance.AutoconnectEnabled;
        Hostname = CrackNetSettings.Instance.AutoconnectHost;
        ServerPort = CrackNetSettings.Instance.AutoconnectPort;
        UseCompression = CrackNetSettings.Instance.UseCompression;
        var settings = CrackNetSettings.Instance;
        Conditions = Profile.Named(settings.SimulatedProfile)
                     ?? new Profile(settings.SimulatedLatencyMs, settings.SimulatedPacketLossChance * 100.0,
                         settings.SimulatedJitterMs, BurstLossMs: settings.SimulatedBurstLossMs,
                         BurstIntervalSeconds: settings.SimulatedBurstIntervalSeconds);
        LatencyMs = Conditions.LatencyMs;
        PacketLossPercent = Conditions.PacketLossPercent;
    }
}
