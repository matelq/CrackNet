#if TOOLS
using Godot;
using Godot.Collections;

namespace CrackNet.Editor;

/// <summary>Registers CrackNet project settings and autoloads. Custom node types come from [GlobalClass]. Port of netfox.gd.</summary>
[Tool]
public partial class CrackNetPlugin : EditorPlugin
{
    private const string Root = "res://addons/cracknet";

    private sealed record Setting(string Name, Variant Value, Variant.Type Type, PropertyHint Hint = PropertyHint.None, string HintString = "");

    private static Setting LogLevelSetting(string name)
        => new(name, (int)Core.Logging.CrackNetLogger.DefaultLogLevel, Variant.Type.Int, PropertyHint.Enum, "All,Trace,Debug,Info,Warning,Error,None");

    private static readonly Setting[] Settings =
    [
        // Under the path MTU with room for IP, UDP and transport headers: the limit Steam uses and Gaffer on Games
        // recommends, so a state packet is never fragmented
        new("cracknet/general/max_sync_packet_size", 1200, Variant.Type.Int, PropertyHint.Range, "64,1400,or_greater"),

        LogLevelSetting("cracknet/logging/cracknet_log_level"),

        // The tick is the physics step, so this is how often state goes on the wire: 2 is 30 a second at 60 Hz physics
        new("cracknet/time/state_interval_ticks", 2, Variant.Type.Int, PropertyHint.Range, "1,8,or_greater"),

        // Extras: window tiler
        new("cracknet/extras/auto_tile_windows", false, Variant.Type.Bool),
        new("cracknet/extras/tile_screen", 0, Variant.Type.Int, PropertyHint.Range, "0,3,or_greater"),
        new("cracknet/extras/tile_borderless", true, Variant.Type.Bool),

        // Extras: autoconnect / network simulator
        new("cracknet/autoconnect/enabled", false, Variant.Type.Bool),
        new("cracknet/autoconnect/simulated_profile", "Realistic", Variant.Type.String, PropertyHint.Enum, "Clear,Casual,Realistic,Bad,Hostile"),
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

    }

    public override void _ExitTree()
    {
        // The settings stay in project.godot: disabling the plugin for one run used to wipe every value a user had set
        foreach (var (name, _) in Autoloads)
            if (HasAutoload(name)) RemoveAutoloadSingleton(name);
    }

    private static void AddSetting(Setting setting)
    {
        // The type and hint are not saved to project.godot, so they have to be registered on every load: skipping a
        // setting that already has a value left an enum as a bare string field
        if (!ProjectSettings.HasSetting(setting.Name)) ProjectSettings.SetSetting(setting.Name, setting.Value);
        ProjectSettings.SetInitialValue(setting.Name, setting.Value);
        ProjectSettings.AddPropertyInfo(new Dictionary
        {
            ["name"] = setting.Name,
            ["type"] = (int)setting.Type,
            ["hint"] = (int)setting.Hint,
            ["hint_string"] = setting.HintString,
        });
    }

    private static bool HasAutoload(string name) => ProjectSettings.HasSetting("autoload/" + name);
}
#endif
