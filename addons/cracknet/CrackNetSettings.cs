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

    // general
    public bool UseRawCommands { get; set; }
    public int MaxSyncPacketSize { get; set; } = 1200;
    public bool SuppressIdentityPeerDisconnectedWarning { get; set; }

    // logging
    public LogLevel LogLevel { get; set; } = CrackNetLogger.DefaultLogLevel;
    public LogLevel CrackNetLogLevel { get; set; } = CrackNetLogger.DefaultLogLevel;
    public LogLevel CrackNetExtrasLogLevel { get; set; } = CrackNetLogger.DefaultLogLevel;

    // time
    public int Tickrate { get; set; } = 30;
    public int MaxTicksPerFrame { get; set; } = 8;
    public double RecalibrateThreshold { get; set; } = 8.0;

    /// <summary>
    /// Same <c>cracknet/time/recalibrate_threshold</c> key as <see cref="RecalibrateThreshold"/>, but with the fallback
    /// upstream uses in the time synchronizer (<c>network-time-synchronizer.gd:105</c>). The two differ only when the
    /// setting is absent.
    /// </summary>
    public double SyncPanicThreshold { get; set; } = 2.0;
    public double StallThreshold { get; set; } = 1.0;
    public double SyncInterval { get; set; } = 0.25;
    public int SyncSamples { get; set; } = 8;
    public int SyncAdjustSteps { get; set; } = 8;
    public bool SyncToPhysics { get; set; } = true;
    public double MaxTimeStretch { get; set; } = 1.25;
    public TickrateMismatchAction TickrateMismatchAction { get; set; } = TickrateMismatchAction.Warn;
    public bool SuppressOfflinePeerWarning { get; set; }

    // events
    public bool EventsEnabled { get; set; } = true;

    // extras
    public bool AutoTileWindows { get; set; }
    public int TileScreen { get; set; }
    public bool Borderless { get; set; }

    // autoconnect
    public bool AutoconnectEnabled { get; set; }
    public string AutoconnectHost { get; set; } = "127.0.0.1";
    public int AutoconnectPort { get; set; } = 9999;
    public bool UseCompression { get; set; }
    public int SimulatedLatencyMs { get; set; }
    public double SimulatedPacketLossChance { get; set; }

    /// <summary>A named NetworkSimulator profile, or "Custom" for the latency, loss, jitter and burst settings.</summary>
    public string SimulatedProfile { get; set; } = "Bad";
    public int SimulatedJitterMs { get; set; }
    public int SimulatedBurstLossMs { get; set; }
    public double SimulatedBurstIntervalSeconds { get; set; }

    /// <summary>Reads every setting from <c>ProjectSettings</c>, falling back to the defaults the plugin registers.</summary>
    public static CrackNetSettings Load()
    {
        var s = new CrackNetSettings();

        s.UseRawCommands = Settings.GetBool("cracknet/general/use_raw_commands", s.UseRawCommands);
        s.MaxSyncPacketSize = Settings.GetInt("cracknet/general/max_sync_packet_size", s.MaxSyncPacketSize);
        s.SuppressIdentityPeerDisconnectedWarning = Settings.GetBool(
            "cracknet/general/supress_identity_peer_disconnected_warning", s.SuppressIdentityPeerDisconnectedWarning);

        s.LogLevel = (LogLevel)Settings.GetInt("cracknet/logging/log_level", (int)s.LogLevel);
        s.CrackNetLogLevel = (LogLevel)Settings.GetInt("cracknet/logging/cracknet_log_level", (int)s.CrackNetLogLevel);
        s.CrackNetExtrasLogLevel = (LogLevel)Settings.GetInt("cracknet/logging/cracknet_extras_log_level", (int)s.CrackNetExtrasLogLevel);

        s.Tickrate = Settings.GetInt("cracknet/time/tickrate", s.Tickrate);
        s.MaxTicksPerFrame = Settings.GetInt("cracknet/time/max_ticks_per_frame", s.MaxTicksPerFrame);
        s.RecalibrateThreshold = Settings.GetDouble("cracknet/time/recalibrate_threshold", s.RecalibrateThreshold);
        s.SyncPanicThreshold = Settings.GetDouble("cracknet/time/recalibrate_threshold", s.SyncPanicThreshold);
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
        s.TileScreen = Settings.GetInt("cracknet/extras/screen", s.TileScreen);
        s.Borderless = Settings.GetBool("cracknet/extras/borderless", s.Borderless);

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
