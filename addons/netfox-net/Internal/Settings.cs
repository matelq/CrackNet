using Godot;

namespace Netfox.Internal;

/// <summary>Typed access to ProjectSettings with defaults, mirroring ProjectSettings.get_setting(key, default).</summary>
internal static class Settings
{
    public static int GetInt(string key, int fallback) => Has(key) ? ProjectSettings.GetSetting(key).AsInt32() : fallback;
    public static double GetDouble(string key, double fallback) => Has(key) ? ProjectSettings.GetSetting(key).AsDouble() : fallback;
    public static bool GetBool(string key, bool fallback) => Has(key) ? ProjectSettings.GetSetting(key).AsBool() : fallback;
    public static string GetString(string key, string fallback) => Has(key) ? ProjectSettings.GetSetting(key).AsString() : fallback;

    private static bool Has(string key) => ProjectSettings.HasSetting(key);
}
