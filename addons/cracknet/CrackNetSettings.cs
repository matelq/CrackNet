using CrackNet.Core.Logging;
using CrackNet.Internal;

namespace CrackNet;

/// <summary>
/// Every <c>cracknet/*</c> project setting, read once into one mutable object.
/// <para>
/// Upstream reads <c>ProjectSettings</c> in field initializers of each server (see <c>network-time.gd:370</c>), which ties
/// the servers to <c>project.godot</c> and makes runtime toggles ad hoc. Servers take their values from
/// <see cref="Instance"/> instead; assign a different instance before the autoloads are created to configure them.
/// </para>
/// </summary>
public sealed class CrackNetSettings
{
    /// <summary>The settings the autoloads use. Replace before they enter the tree; mutating it later only affects re-reads.</summary>
    public static CrackNetSettings Instance { get; set; } = Load();

    // general (cracknet/general/*)

    /// <summary>Send commands as raw packets instead of RPCs. Cheaper per command, but invisible to Godot's RPC tooling.</summary>
    public bool UseRawCommands { get; set; }

    /// <summary>Bytes a state packet may reach before it is split. 1200 stays under the path MTU with room for IP, UDP and transport headers.</summary>
    public int MaxSyncPacketSize { get; set; } = 1200;

    /// <summary>Drop the warning the identity server logs when a peer disconnects without an identity.</summary>
    public bool SuppressIdentityPeerDisconnectedWarning { get; set; }

    // logging (cracknet/logging/*)

    /// <summary>Lowest level any logger prints.</summary>
    public LogLevel LogLevel { get; set; } = CrackNetLogger.DefaultLogLevel;

    /// <summary>Lowest level the addon's own loggers print.</summary>
    public LogLevel CrackNetLogLevel { get; set; } = CrackNetLogger.DefaultLogLevel;

    /// <summary>Lowest level the extras (window tiler, network simulator) print.</summary>
    public LogLevel CrackNetExtrasLogLevel { get; set; } = CrackNetLogger.DefaultLogLevel;

    // time (cracknet/time/*)

    /// <summary>Network ticks per second. Every peer must agree; see <see cref="TickrateMismatchAction"/>.</summary>
    public int Tickrate { get; set; } = 30;

    /// <summary>Ticks a single frame may run before the clock gives up catching up.</summary>
    public int MaxTicksPerFrame { get; set; } = 8;

    /// <summary>Seconds of clock error after which the local clock is snapped to the remote one instead of stretched towards it.</summary>
    public double RecalibrateThreshold { get; set; } = 8.0;

    /// <summary>Seconds a frame may take before it counts as a stall and its time is discarded rather than ticked through.</summary>
    public double StallThreshold { get; set; } = 1.0;

    /// <summary>Seconds between clock sync exchanges.</summary>
    public double SyncInterval { get; set; } = 0.25;

    /// <summary>Round trips averaged into one clock offset estimate.</summary>
    public int SyncSamples { get; set; } = 8;

    /// <summary>Ticks over which a measured offset is applied, so the clock eases rather than jumps.</summary>
    public int SyncAdjustSteps { get; set; } = 8;

    /// <summary>Drive ticks from <c>_PhysicsProcess</c> instead of <c>_Process</c>.</summary>
    public bool SyncToPhysics { get; set; } = true;

    /// <summary>Fastest the clock may run while catching up, as a multiple of real time.</summary>
    public double MaxTimeStretch { get; set; } = 1.25;

    /// <summary>What a peer does when another peer reports a different tickrate.</summary>
    public TickrateMismatchAction TickrateMismatchAction { get; set; } = TickrateMismatchAction.Warn;

    /// <summary>Drop the warning logged when the clock is asked about a peer that is no longer connected.</summary>
    public bool SuppressOfflinePeerWarning { get; set; }

    // events (cracknet/events/*)

    /// <summary>Emit the <c>NetworkEvents</c> signals at all.</summary>
    public bool EventsEnabled { get; set; } = true;

    // extras: window tiler (cracknet/extras/*)

    /// <summary>Arrange the windows of the running instances side by side, so several peers are visible at once.</summary>
    public bool AutoTileWindows { get; set; }

    /// <summary>Index of the screen the tiler lays the windows out on.</summary>
    public int TileScreen { get; set; }

    /// <summary>Give the tiled windows no title bar, so more of each one is game.</summary>
    public bool TileBorderless { get; set; }

    // extras: autoconnect and the network simulator (cracknet/autoconnect/*) - a playtest tool, off in a shipped game

    /// <summary>The first instance hosts and the rest join it on start, with no menu.</summary>
    public bool AutoconnectEnabled { get; set; }

    /// <summary>Address the joining instances connect to.</summary>
    public string AutoconnectHost { get; set; } = "127.0.0.1";

    /// <summary>Port autoconnect hosts and joins on.</summary>
    public int AutoconnectPort { get; set; } = 9999;

    /// <summary>Compress the autoconnect peer's traffic.</summary>
    public bool UseCompression { get; set; }

    /// <summary>One-way delay the simulator adds, in milliseconds.</summary>
    public int SimulatedLatencyMs { get; set; }

    /// <summary>Share of packets the simulator drops, 0 to 1.</summary>
    public double SimulatedPacketLossChance { get; set; }

    /// <summary>A named NetworkSimulator profile, or "Custom" for the latency, loss, jitter and burst settings.</summary>
    public string SimulatedProfile { get; set; } = "Bad";

    /// <summary>Random variation added to the simulated latency, in milliseconds.</summary>
    public int SimulatedJitterMs { get; set; }

    /// <summary>Length of a simulated outage, in milliseconds.</summary>
    public int SimulatedBurstLossMs { get; set; }

    /// <summary>Seconds between simulated outages. Zero means none.</summary>
    public double SimulatedBurstIntervalSeconds { get; set; }

    /// <summary>Reads every setting from <c>ProjectSettings</c>, falling back to the defaults the plugin registers.</summary>
    public static CrackNetSettings Load()
    {
        var s = new CrackNetSettings();

        s.UseRawCommands = Settings.GetBool("cracknet/general/use_raw_commands", s.UseRawCommands);
        s.MaxSyncPacketSize = Settings.GetInt("cracknet/general/max_sync_packet_size", s.MaxSyncPacketSize);
        s.SuppressIdentityPeerDisconnectedWarning = Settings.GetBool(
            "cracknet/general/suppress_identity_peer_disconnected_warning", s.SuppressIdentityPeerDisconnectedWarning);

        s.LogLevel = (LogLevel)Settings.GetInt("cracknet/logging/log_level", (int)s.LogLevel);
        s.CrackNetLogLevel = (LogLevel)Settings.GetInt("cracknet/logging/cracknet_log_level", (int)s.CrackNetLogLevel);
        s.CrackNetExtrasLogLevel = (LogLevel)Settings.GetInt("cracknet/logging/cracknet_extras_log_level", (int)s.CrackNetExtrasLogLevel);

        s.Tickrate = Settings.GetInt("cracknet/time/tickrate", s.Tickrate);
        s.MaxTicksPerFrame = Settings.GetInt("cracknet/time/max_ticks_per_frame", s.MaxTicksPerFrame);
        s.RecalibrateThreshold = Settings.GetDouble("cracknet/time/recalibrate_threshold", s.RecalibrateThreshold);
        s.StallThreshold = Settings.GetDouble("cracknet/time/stall_threshold", s.StallThreshold);
        s.SyncInterval = Settings.GetDouble("cracknet/time/sync_interval", s.SyncInterval);
        s.SyncSamples = Settings.GetInt("cracknet/time/sync_samples", s.SyncSamples);
        s.SyncAdjustSteps = Settings.GetInt("cracknet/time/sync_adjust_steps", s.SyncAdjustSteps);
        s.SyncToPhysics = Settings.GetBool("cracknet/time/sync_to_physics", s.SyncToPhysics);
        s.MaxTimeStretch = Settings.GetDouble("cracknet/time/max_time_stretch", s.MaxTimeStretch);
        s.TickrateMismatchAction = (TickrateMismatchAction)Settings.GetInt(
            "cracknet/time/tickrate_mismatch_action", (int)s.TickrateMismatchAction);
        s.SuppressOfflinePeerWarning = Settings.GetBool("cracknet/time/suppress_offline_peer_warning", s.SuppressOfflinePeerWarning);

        s.EventsEnabled = Settings.GetBool("cracknet/events/enabled", s.EventsEnabled);

        s.AutoTileWindows = Settings.GetBool("cracknet/extras/auto_tile_windows", s.AutoTileWindows);
        s.TileScreen = Settings.GetInt("cracknet/extras/tile_screen", s.TileScreen);
        s.TileBorderless = Settings.GetBool("cracknet/extras/tile_borderless", s.TileBorderless);

        s.AutoconnectEnabled = Settings.GetBool("cracknet/autoconnect/enabled", s.AutoconnectEnabled);
        s.AutoconnectHost = Settings.GetString("cracknet/autoconnect/host", s.AutoconnectHost);
        s.AutoconnectPort = Settings.GetInt("cracknet/autoconnect/port", s.AutoconnectPort);
        s.UseCompression = Settings.GetBool("cracknet/autoconnect/use_compression", s.UseCompression);
        s.SimulatedLatencyMs = Settings.GetInt("cracknet/autoconnect/simulated_latency_ms", s.SimulatedLatencyMs);
        s.SimulatedPacketLossChance = Settings.GetDouble(
            "cracknet/autoconnect/simulated_packet_loss_chance", s.SimulatedPacketLossChance);
        s.SimulatedProfile = Settings.GetString("cracknet/autoconnect/simulated_profile", s.SimulatedProfile);
        s.SimulatedJitterMs = Settings.GetInt("cracknet/autoconnect/simulated_jitter_ms", s.SimulatedJitterMs);
        s.SimulatedBurstLossMs = Settings.GetInt("cracknet/autoconnect/simulated_burst_loss_ms", s.SimulatedBurstLossMs);
        s.SimulatedBurstIntervalSeconds = Settings.GetDouble(
            "cracknet/autoconnect/simulated_burst_interval_seconds", s.SimulatedBurstIntervalSeconds);

        return s;
    }
}
