using Godot;
using Netfox.Core.Logging;
using Netfox.Core.Time;
using Netfox.Internal;

namespace Netfox;

/// <summary>Custom Performance monitors for the network and rollback loops. Port of network-performance.gd.</summary>
public partial class NetworkPerformance : Node
{
    public static NetworkPerformance Instance { get; private set; } = null!;

    private static readonly NetfoxLogger Logger = NetfoxLogger.ForNetfox("NetworkPerformance");

    public static readonly StringName NetworkLoopDurationMonitor = "netfox/Network loop duration (ms)";
    public static readonly StringName RollbackLoopDurationMonitor = "netfox/Rollback loop duration (ms)";
    public static readonly StringName NetworkTicksMonitor = "netfox/Network ticks simulated";
    public static readonly StringName RollbackTicksMonitor = "netfox/Rollback ticks simulated";
    public static readonly StringName RollbackTickDurationMonitor = "netfox/Rollback tick duration (ms)";
    public static readonly StringName RollbackNodesSimulatedMonitor = "netfox/Rollback nodes simulated";
    public static readonly StringName RollbackNodesSimulatedPerTickMonitor = "netfox/Rollback nodes simulated per tick (avg)";
    public static readonly StringName FullStatePropertiesCount = "netfox/Full state properties count";
    public static readonly StringName SentStatePropertiesCount = "netfox/Sent state properties count";
    public static readonly StringName SentStatePropertiesRatio = "netfox/Sent state properties ratio";

    private double _networkLoopStart;
    private double _networkLoopDuration;
    private int _networkTicks;
    private int _networkTicksAccum;

    private double _rollbackLoopStart;
    private double _rollbackLoopDuration;
    private int _rollbackTicks;
    private int _rollbackTicksAccum;

    private int _rollbackNodesSimulated;
    private int _rollbackNodesSimulatedAccum;

    private int _fullStateProps;
    private int _fullStatePropsAccum;
    private int _sentStateProps;
    private int _sentStatePropsAccum;

    public bool IsEnabled()
    {
        if (OS.HasFeature("netfox_noperf")) return false;
        if (OS.HasFeature("netfox_perf")) return true;
        return OS.IsDebugBuild();
    }

    public double GetNetworkLoopDurationMs() => _networkLoopDuration * 1000.0;
    public int GetNetworkTicks() => _networkTicks;
    public double GetRollbackLoopDurationMs() => _rollbackLoopDuration * 1000.0;
    public int GetRollbackTicks() => _rollbackTicks;
    public double GetRollbackTickDurationMs() => _rollbackLoopDuration * 1000.0 / Math.Max(_rollbackTicks, 1);
    public int GetRollbackNodesSimulated() => _rollbackNodesSimulated;
    public double GetRollbackNodesSimulatedPerTick() => _rollbackNodesSimulated / Math.Max(1.0, _rollbackTicks);
    public int GetFullStatePropsCount() => _fullStateProps;
    public int GetSentStatePropsCount() => _sentStateProps;
    public double GetSentStatePropsRatio() => _sentStateProps / Math.Max(1.0, _fullStateProps);

    public void PushRollbackNodesSimulated(int count) => _rollbackNodesSimulatedAccum += count;
    public void PushFullStateProps(int count) => _fullStatePropsAccum += count;
    public void PushSentStateProps(int count) => _sentStatePropsAccum += count;

    public override void _EnterTree()
    {
        NetfoxRuntime.EnsureInitialized();
        Instance ??= this;
    }

    public override void _Ready()
    {
        if (!IsEnabled())
        {
            Logger.Debug("Network performance disabled");
            return;
        }

        Logger.Debug("Network performance enabled, registering performance monitors");
        Performance.AddCustomMonitor(NetworkLoopDurationMonitor, Callable.From(GetNetworkLoopDurationMs));
        Performance.AddCustomMonitor(RollbackLoopDurationMonitor, Callable.From(GetRollbackLoopDurationMs));
        Performance.AddCustomMonitor(NetworkTicksMonitor, Callable.From(GetNetworkTicks));
        Performance.AddCustomMonitor(RollbackTicksMonitor, Callable.From(GetRollbackTicks));
        Performance.AddCustomMonitor(RollbackTickDurationMonitor, Callable.From(GetRollbackTickDurationMs));
        Performance.AddCustomMonitor(RollbackNodesSimulatedMonitor, Callable.From(GetRollbackNodesSimulated));
        Performance.AddCustomMonitor(RollbackNodesSimulatedPerTickMonitor, Callable.From(GetRollbackNodesSimulatedPerTick));
        Performance.AddCustomMonitor(FullStatePropertiesCount, Callable.From(GetFullStatePropsCount));
        Performance.AddCustomMonitor(SentStatePropertiesCount, Callable.From(GetSentStatePropsCount));
        Performance.AddCustomMonitor(SentStatePropertiesRatio, Callable.From(GetSentStatePropsRatio));

        var networkTime = NetworkTime.Instance;
        networkTime.BeforeTickLoop += BeforeTickLoop;
        networkTime.OnTick += OnNetworkTick;
        networkTime.AfterTickLoop += AfterTickLoop;

        var rollback = NetworkRollback.Instance;
        rollback.BeforeLoop += BeforeRollbackLoop;
        rollback.OnProcessTick += OnRollbackTick;
        rollback.AfterLoop += AfterRollbackLoop;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null!;
    }

    private void BeforeTickLoop()
    {
        _networkLoopStart = Clocks.UnixTime();
        _networkTicksAccum = 0;
    }

    private void OnNetworkTick(double delta, int tick) => _networkTicksAccum++;

    private void AfterTickLoop()
    {
        _networkLoopDuration = Clocks.UnixTime() - _networkLoopStart;
        _networkTicks = _networkTicksAccum;

        _fullStateProps = _fullStatePropsAccum;
        _fullStatePropsAccum = 0;
        _sentStateProps = _sentStatePropsAccum;
        _sentStatePropsAccum = 0;
    }

    private void BeforeRollbackLoop()
    {
        _rollbackLoopStart = Clocks.UnixTime();
        _rollbackTicksAccum = 0;
        _rollbackNodesSimulatedAccum = 0;
    }

    private void OnRollbackTick(int tick) => _rollbackTicksAccum++;

    private void AfterRollbackLoop()
    {
        _rollbackLoopDuration = Clocks.UnixTime() - _rollbackLoopStart;
        _rollbackTicks = _rollbackTicksAccum;
        _rollbackNodesSimulated = _rollbackNodesSimulatedAccum;
    }
}
