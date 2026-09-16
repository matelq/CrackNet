#if TOOLS
using Godot;
using Godot.Collections;

namespace Netfox.Editor;

/// <summary>Registers netfox project settings and autoloads. Custom node types come from [GlobalClass]. Port of netfox.gd.</summary>
[Tool]
public partial class NetfoxPlugin : EditorPlugin
{
    private const string Root = "res://addons/netfox-net";

    private sealed record Setting(string Name, Variant Value, Variant.Type Type, PropertyHint Hint = PropertyHint.None, string HintString = "");

    private static Setting LogLevelSetting(string name)
        => new(name, (int)Core.Logging.NetfoxLogger.DefaultLogLevel, Variant.Type.Int, PropertyHint.Enum, "All,Trace,Debug,Info,Warning,Error,None");

    private static readonly Setting[] Settings =
    [
        // Setting this to false makes netfox keep its settings when disabling the plugin
        new("netfox/general/clear_settings", true, Variant.Type.Bool),
        new("netfox/general/use_raw_commands", false, Variant.Type.Bool),
        // Very conservative packet size limit, source: https://stackoverflow.com/a/35697810
        new("netfox/general/max_sync_packet_size", 508, Variant.Type.Int),
        new("netfox/general/supress_identity_peer_disconnected_warning", false, Variant.Type.Bool),

        LogLevelSetting("netfox/logging/log_level"),
        LogLevelSetting("netfox/logging/netfox_log_level"),
        LogLevelSetting("netfox/logging/netfox_extras_log_level"),

        new("netfox/time/tickrate", 30, Variant.Type.Int),
        new("netfox/time/max_ticks_per_frame", 8, Variant.Type.Int),
        new("netfox/time/recalibrate_threshold", 8.0, Variant.Type.Float),
        new("netfox/time/stall_threshold", 1.0, Variant.Type.Float),
        new("netfox/time/sync_interval", 0.25, Variant.Type.Float, PropertyHint.Range, $"{NetworkTimeSynchronizer.MinSyncInterval},2,or_greater"),
        new("netfox/time/sync_samples", 8, Variant.Type.Int),
        new("netfox/time/sync_adjust_steps", 8, Variant.Type.Int),
        new("netfox/time/sync_to_physics", false, Variant.Type.Bool),
        new("netfox/time/max_time_stretch", 1.25, Variant.Type.Float, PropertyHint.Range, "1,2,0.05,or_greater"),
        new("netfox/time/tickrate_mismatch_action", (int)TickrateMismatchAction.Warn, Variant.Type.Int, PropertyHint.Enum, "Warn,Disconnect,Adjust,Signal"),
        new("netfox/time/suppress_offline_peer_warning", false, Variant.Type.Bool),

        new("netfox/events/enabled", true, Variant.Type.Bool),

        // Extras: window tiler
        new("netfox/extras/auto_tile_windows", false, Variant.Type.Bool),
        new("netfox/extras/screen", 0, Variant.Type.Int),
        new("netfox/extras/borderless", false, Variant.Type.Bool),

        // Extras: autoconnect / network simulator
        new("netfox/autoconnect/enabled", false, Variant.Type.Bool),
        new("netfox/autoconnect/host", "127.0.0.1", Variant.Type.String),
        new("netfox/autoconnect/port", 9999, Variant.Type.Int, PropertyHint.Range, "1,65535,hide_slider"),
        new("netfox/autoconnect/use_compression", false, Variant.Type.Bool),
        new("netfox/autoconnect/simulated_profile", "Bad", Variant.Type.String, PropertyHint.Enum, "Clear,Casual,Realistic,Bad,Hostile,Custom"),
        new("netfox/autoconnect/simulated_latency_ms", 0, Variant.Type.Int, PropertyHint.Range, "0,200,or_greater"),
        new("netfox/autoconnect/simulated_packet_loss_chance", 0.0, Variant.Type.Float, PropertyHint.Range, "0,1"),
        new("netfox/autoconnect/simulated_jitter_ms", 0, Variant.Type.Int, PropertyHint.Range, "0,200,or_greater"),
        new("netfox/autoconnect/simulated_burst_loss_ms", 0, Variant.Type.Int, PropertyHint.Range, "0,1000,or_greater"),
        new("netfox/autoconnect/simulated_burst_interval_seconds", 0.0, Variant.Type.Float, PropertyHint.Range, "0,60,or_greater"),
    ];

    // Order matters: dependencies come first
    private static readonly (string Name, string Path)[] Autoloads =
    [
        ("NetworkCommandServer", Root + "/Servers/NetworkCommandServer.cs"),
        ("NetworkTime", Root + "/NetworkTime.cs"),
        ("NetworkTimeSynchronizer", Root + "/NetworkTimeSynchronizer.cs"),
        ("NetworkEvents", Root + "/NetworkEvents.cs"),
        ("NetworkIdentityServer", Root + "/Servers/NetworkIdentityServer.cs"),
        ("NetworkObjectServer", Root + "/Servers/NetworkObjectServer.cs"),
        ("WindowTiler", Root + "/Extras/WindowTiler.cs"),
        ("NetworkSimulator", Root + "/Extras/NetworkSimulator.cs"),
    ];

    public override void _EnterTree()
    {
        foreach (var setting in Settings)
            AddSetting(setting);

        foreach (var (name, path) in Autoloads)
            if (!HasAutoload(name)) AddAutoloadSingleton(name, path);

        _profileFields = ReadProfileFields();
        _profile = ProjectSettings.GetSetting(ProfileKey).AsString();
        _syncProfileFields = Callable.From(SyncProfileFields);
        ProjectSettings.Singleton.Connect(ProjectSettings.SignalName.SettingsChanged, _syncProfileFields);
        SyncProfileFields();
    }

    private const string ProfileKey = "netfox/autoconnect/simulated_profile";
    private static readonly string[] ProfileFieldKeys =
    [
        "netfox/autoconnect/simulated_latency_ms",
        "netfox/autoconnect/simulated_packet_loss_chance",
        "netfox/autoconnect/simulated_jitter_ms",
        "netfox/autoconnect/simulated_burst_loss_ms",
        "netfox/autoconnect/simulated_burst_interval_seconds",
    ];

    private string _profile = "";
    private Callable _syncProfileFields;
    private double[] _profileFields = [];

    private static double[] ReadProfileFields() => ProfileFieldKeys.Select(key => ProjectSettings.GetSetting(key).AsDouble()).ToArray();

    private static double[] FieldsOf(Extras.NetworkSimulator.Profile profile) =>
        [profile.LatencyMs, profile.PacketLossPercent / 100.0, profile.JitterMs, profile.BurstLossMs, profile.BurstIntervalSeconds];

    /// <summary>
    /// Keeps the simulator's number fields showing what the chosen profile means: picking a named profile writes its
    /// values into them, and editing a number away from it switches the profile to Custom.
    /// </summary>
    private void SyncProfileFields()
    {
        var profile = ProjectSettings.GetSetting(ProfileKey).AsString();
        var fields = ReadProfileFields();
        var named = Extras.NetworkSimulator.Profile.Named(profile);

        if (profile != _profile && named is not null && !fields.SequenceEqual(FieldsOf(named)))
        {
            fields = FieldsOf(named);
            for (var i = 0; i < ProfileFieldKeys.Length; i++)
                ProjectSettings.SetSetting(ProfileFieldKeys[i], ProfileFieldKeys[i].EndsWith("_ms") ? (int)fields[i] : fields[i]);
            ProjectSettings.Save();
        }
        else if (profile == _profile && named is not null && !fields.SequenceEqual(_profileFields) && !fields.SequenceEqual(FieldsOf(named)))
        {
            profile = "Custom";
            ProjectSettings.SetSetting(ProfileKey, profile);
            ProjectSettings.Save();
        }

        _profile = profile;
        _profileFields = fields;
    }

    public override void _ExitTree()
    {
        if (ProjectSettings.Singleton.IsConnected(ProjectSettings.SignalName.SettingsChanged, _syncProfileFields))
            ProjectSettings.Singleton.Disconnect(ProjectSettings.SignalName.SettingsChanged, _syncProfileFields);

        if (ProjectSettings.GetSetting("netfox/general/clear_settings", false).AsBool())
            foreach (var setting in Settings)
                RemoveSetting(setting);

        foreach (var (name, _) in Autoloads)
            if (HasAutoload(name)) RemoveAutoloadSingleton(name);
    }

    private static void AddSetting(Setting setting)
    {
        if (ProjectSettings.HasSetting(setting.Name)) return;

        ProjectSettings.SetSetting(setting.Name, setting.Value);
        ProjectSettings.SetInitialValue(setting.Name, setting.Value);
        ProjectSettings.AddPropertyInfo(new Dictionary
        {
            ["name"] = setting.Name,
            ["type"] = (int)setting.Type,
            ["hint"] = (int)setting.Hint,
            ["hint_string"] = setting.HintString,
        });
    }

    private static void RemoveSetting(Setting setting)
    {
        if (ProjectSettings.HasSetting(setting.Name))
            ProjectSettings.Clear(setting.Name);
    }

    private static bool HasAutoload(string name) => ProjectSettings.HasSetting("autoload/" + name);
}
#endif
