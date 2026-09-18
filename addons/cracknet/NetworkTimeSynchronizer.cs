using CrackNet.Core.Logging;
using CrackNet.Core.Serialization;
using CrackNet.Core.Time;
using CrackNet.Internal;
using Godot;

namespace CrackNet;

/// <summary>
/// Continuously synchronizes the reference clock to the host. Transport and timing live here,
/// the clock math lives in CrackNet.Core.Time.ClockSynchronizer. Port of network-time-synchronizer.gd.
/// </summary>
public partial class NetworkTimeSynchronizer : Node
{
    public static NetworkTimeSynchronizer Instance { get; private set; } = null!;

    /// <summary>The stack this server belongs to; resolved when it enters the tree.</summary>
    public CrackNetContext Context { get; private set; } = CrackNetContext.Default;

    public const double MinSyncInterval = ClockSynchronizer.MinSyncInterval;

    private static readonly CrackNetLogger Logger = CrackNetLogger.ForCrackNet("NetworkTimeSynchronizer");

    private readonly ClockSynchronizer _sync;
    private NetworkCommandServer? _commandServer;
    private NetworkCommandServer.Command _cmdPing = null!;
    private NetworkCommandServer.Command _cmdPong = null!;
    private NetworkCommandServer.Command _cmdRequestTime = null!;
    private NetworkCommandServer.Command _cmdSetTime = null!;
    private bool _active;

    /// <summary>Emitted once the initial timestamp is received and the sync loop starts.</summary>
    public event Action? OnInitialSync;

    /// <summary>Emitted when clocks are so far apart that the clock gets hard-reset. Carries the offset.</summary>
    public event Action<double>? OnPanic;

    public NetworkTimeSynchronizer() : this(null) { }

    public NetworkTimeSynchronizer(NetworkCommandServer? commandServer)
    {
        _commandServer = commandServer;
        _sync = new ClockSynchronizer
        {
            SyncInterval = CrackNetSettings.Instance.SyncInterval,
            SyncSamples = CrackNetSettings.Instance.SyncSamples,
            AdjustSteps = CrackNetSettings.Instance.SyncAdjustSteps,
            PanicThreshold = CrackNetSettings.Instance.SyncPanicThreshold,
        };
        _sync.OnPanic += offset => OnPanic?.Invoke(offset);
    }

    /// <summary>Time between sync samples in seconds, never below MinSyncInterval. Configured in project settings.</summary>
    public double SyncInterval => _sync.SyncInterval;
    public int SyncSamples => _sync.SyncSamples;
    public int AdjustSteps => _sync.AdjustSteps;
    public double PanicThreshold => _sync.PanicThreshold;

    /// <summary>Measured roundtrip time to the host; actual values are within Rtt +/- RttJitter.</summary>
    public double Rtt => _sync.Rtt;
    public double RttJitter => _sync.RttJitter;
    /// <summary>Estimated offset from the host clock. Positive means the host is ahead.</summary>
    public double RemoteOffset => _sync.RemoteOffset;

    public override void _EnterTree()
    {
        CrackNetRuntime.EnsureInitialized();
        Context = CrackNetContext.For(this);
        Context.NetworkTimeSynchronizer ??= this;
        if (Context.IsDefault) Instance ??= this;
    }

    public override void _Ready()
    {
        _commandServer ??= Context.NetworkCommandServer;
        _cmdPing = _commandServer.RegisterCommandAt(CommandIds.Ping, HandlePing, MultiplayerPeer.TransferModeEnum.Unreliable);
        _cmdPong = _commandServer.RegisterCommandAt(CommandIds.Pong, HandlePong, MultiplayerPeer.TransferModeEnum.Unreliable);
        _cmdRequestTime = _commandServer.RegisterCommandAt(CommandIds.RequestTime, HandleRequestTimestamp, MultiplayerPeer.TransferModeEnum.Reliable);
        _cmdSetTime = _commandServer.RegisterCommandAt(CommandIds.SetTime, HandleSetTimestamp, MultiplayerPeer.TransferModeEnum.Reliable);
    }

    public override void _ExitTree()
    {
        if (ReferenceEquals(Context.NetworkTimeSynchronizer, this)) Context.NetworkTimeSynchronizer = null!;
        if (Instance == this) Instance = null!;
    }

    /// <summary>Start the sync loop. Starting multiple times has no effect.</summary>
    public void Start()
    {
        if (_active) return;

        _sync.Reset();

        if (!Multiplayer.IsServer())
        {
            _active = true;
            _cmdRequestTime.Send(Array.Empty<byte>(), 1);
        }
    }

    public void Stop() => _active = false;

    /// <summary>Current time of the reference clock, in seconds.</summary>
    public double GetTime() => _sync.Time;

    private async Task Loop()
    {
        Logger.Info("Time sync loop started! Initial timestamp: {0}s", _sync.Time);
        OnInitialSync?.Invoke();

        while (_active)
        {
            if (Multiplayer.IsServer())
            {
                Stop();
                return;
            }

            var idx = _sync.BeginSample();
            var ping = new ByteWriter(4);
            ping.PutU32((uint)idx);
            _cmdPing.Send(ping.ToArray(), 1);

            await ToSignal(GetTree().CreateTimer(SyncInterval), SceneTreeTimer.SignalName.Timeout);
            if (!IsInsideTree()) return;
        }
    }

    private void HandlePing(int sender, byte[] data)
    {
        var idx = new ByteReader(data).GetU32();
        var pingReceived = _sync.Time;

        var pong = new ByteWriter(20);
        pong.PutU32(idx);
        pong.PutDouble(pingReceived);
        pong.PutDouble(_sync.Time);
        _cmdPong.Send(pong.ToArray(), sender);
    }

    private void HandlePong(int sender, byte[] data)
    {
        var reader = new ByteReader(data);
        var idx = (int)reader.GetU32();
        var pingReceived = reader.GetDouble();
        var pongSent = reader.GetDouble();

        // Returns null if the sample was dropped mid-flight during a panic episode
        _sync.CompleteSample(idx, pingReceived, pongSent);
    }

    private void HandleRequestTimestamp(int sender, byte[] data)
    {
        Logger.Debug("Requested initial timestamp @ {0:F4}s raw time", _sync.Clock.RawTime);
        var buffer = new ByteWriter(8);
        buffer.PutDouble(_sync.Time);
        _cmdSetTime.Send(buffer.ToArray(), sender);
    }

    private void HandleSetTimestamp(int sender, byte[] data)
    {
        var timestamp = new ByteReader(data).GetDouble();
        Logger.Debug("Received initial timestamp @ {0:F4}s raw time", _sync.Clock.RawTime);
        _sync.Clock.SetTime(timestamp);
        _ = Loop();
    }
}
