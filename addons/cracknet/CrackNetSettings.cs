using CrackNet.Core.Logging;
using CrackNet.Internal;

namespace CrackNet;

/// <summary>
/// CrackNet's configuration, read once into one mutable object.
/// <para>
/// Eight of these are <c>cracknet/*</c> project settings, because they are read before any game code runs or belong to
/// the editor playtest. The rest are tuning numbers with no project setting behind them: a game that needs a different
/// one assigns <see cref="Instance"/> before the autoloads enter the tree, from an autoload of its own ordered above
/// CrackNet's. Each one says here what breaks if it is wrong, which is why it is not offered in the editor.
/// </para>
/// </summary>
public sealed class CrackNetSettings
{
    /// <summary>The settings the autoloads use. Replace before they enter the tree; mutating it later only affects re-reads.</summary>
    public static CrackNetSettings Instance { get; set; } = Load();

    // Project settings (cracknet/*)

    /// <summary>Bytes a state packet may reach before it is split. 1200 stays under the path MTU with room for IP, UDP and transport headers.</summary>
    public int MaxSyncPacketSize { get; set; } = 1200;

    /// <summary>Lowest level the addon's own loggers print.</summary>
    public LogLevel CrackNetLogLevel { get; set; } = CrackNetLogger.DefaultLogLevel;

    /// <summary>
    /// Physics ticks between two state snapshots: 2 sends 30 times a second at Godot's default 60 Hz physics. The tick
    /// is the physics step, so this is the one knob over how often state goes on the wire. Every peer must agree on it;
    /// a differing physics rate numbers the ticks differently and is caught by the tickrate handshake.
    /// </summary>
    public int StateIntervalTicks { get; set; } = 2;

    /// <summary>
    /// Arrange the windows of the running instances side by side, so several peers are visible at once. Off unless a
    /// playtest turns it on: a library that moves a game's window unasked is not one.
    /// </summary>
    public bool AutoTileWindows { get; set; }

    /// <summary>Index of the screen the tiler lays the windows out on.</summary>
    public int TileScreen { get; set; }

    /// <summary>Give the tiled windows no title bar, so more of each one is game.</summary>
    public bool TileBorderless { get; set; } = true;

    /// <summary>The first instance hosts and the rest join it on start, with no menu.</summary>
    public bool AutoconnectEnabled { get; set; }

    /// <summary>A named NetworkSimulator profile, or "Custom" for <see cref="SimulatedLatencyMs"/> and the rest.</summary>
    public string SimulatedProfile { get; set; } = "Clear";

    // Tuning, code only

    /// <summary>
    /// Ticks a single frame may run before the clock gives up catching up. A frame that hung for 300 ms owes nine
    /// ticks; without a ceiling, running them makes the frame longer still, which owes more ticks. 8 ends that spiral
    /// by dropping the rest of the debt.
    /// </summary>
    public int MaxTicksPerFrame { get; set; } = 8;

    /// <summary>
    /// Seconds of clock error after which the local clock is snapped to the remote one instead of eased towards it.
    /// A laptop back from sleep is minutes out, and easing that over <see cref="SyncAdjustSteps"/> would play minutes
    /// of wrong time first; past this the error is not drift and is taken in one step.
    /// </summary>
    public double RecalibrateThreshold { get; set; } = 8.0;

    /// <summary>
    /// Seconds a frame may take before it counts as a stall and its time is discarded rather than ticked through.
    /// Five seconds on a breakpoint is one frame to the engine, and without this the game would simulate those five
    /// seconds on resume.
    /// </summary>
    public double StallThreshold { get; set; } = 1.0;

    /// <summary>Seconds between clock sync exchanges: four measurements a second of how far this peer's clock is from the host's.</summary>
    public double SyncInterval { get; set; } = 0.25;

    /// <summary>
    /// Round trips averaged into one clock offset estimate, weighted by log(RTT); the round trip and its jitter are
    /// the middle and half-spread of the window. Wider is steadier and slower to react.
    /// </summary>
    public int SyncSamples { get; set; } = 8;

    /// <summary>
    /// Ticks over which a measured offset is applied: an 80 ms error is taken 10 ms at a time, so what the clock draws
    /// does not jump. One clock per peer, not one per object.
    /// </summary>
    public int SyncAdjustSteps { get; set; } = 8;

    /// <summary>Fastest the clock may run while catching up, as a multiple of real time.</summary>
    public double MaxTimeStretch { get; set; } = 1.25;

    /// <summary>What a peer does when another peer reports a different tickrate. The handshake warns unasked.</summary>
    public TickrateMismatchAction TickrateMismatchAction { get; set; } = TickrateMismatchAction.Warn;

    /// <summary>Emit the <c>NetworkEvents</c> signals at all.</summary>
    public bool EventsEnabled { get; set; } = true;

    /// <summary>Address the autoconnecting instances connect to.</summary>
    public string AutoconnectHost { get; set; } = "127.0.0.1";

    /// <summary>Port autoconnect hosts and joins on.</summary>
    public int AutoconnectPort { get; set; } = 9999;

    /// <summary>One-way delay the simulator adds, in milliseconds. Read only by the "Custom" profile.</summary>
    public int SimulatedLatencyMs { get; set; }

    /// <summary>Share of packets the simulator drops, 0 to 1. Read only by the "Custom" profile.</summary>
    public double SimulatedPacketLossChance { get; set; }

    /// <summary>Random variation added to the simulated latency, in milliseconds. Read only by the "Custom" profile.</summary>
    public int SimulatedJitterMs { get; set; }

    /// <summary>Length of a simulated outage, in milliseconds. Read only by the "Custom" profile.</summary>
    public int SimulatedBurstLossMs { get; set; }

    /// <summary>Seconds between simulated outages. Zero means none. Read only by the "Custom" profile.</summary>
    public double SimulatedBurstIntervalSeconds { get; set; }

    /// <summary>Reads the eight project settings, falling back to the defaults the plugin registers.</summary>
    public static CrackNetSettings Load()
    {
        var s = new CrackNetSettings();

        s.MaxSyncPacketSize = Settings.GetInt("cracknet/general/max_sync_packet_size", s.MaxSyncPacketSize);

        s.CrackNetLogLevel = (LogLevel)Settings.GetInt("cracknet/logging/cracknet_log_level", (int)s.CrackNetLogLevel);

        s.StateIntervalTicks = Settings.GetInt("cracknet/time/state_interval_ticks", s.StateIntervalTicks);

        s.AutoTileWindows = Settings.GetBool("cracknet/extras/auto_tile_windows", s.AutoTileWindows);
        s.TileScreen = Settings.GetInt("cracknet/extras/tile_screen", s.TileScreen);
        s.TileBorderless = Settings.GetBool("cracknet/extras/tile_borderless", s.TileBorderless);

        s.AutoconnectEnabled = Settings.GetBool("cracknet/autoconnect/enabled", s.AutoconnectEnabled);
        s.SimulatedProfile = Settings.GetString("cracknet/autoconnect/simulated_profile", s.SimulatedProfile);

        return s;
    }
}
