using System.Collections;
using Godot;
using Netfox.Core.Logging;

namespace Netfox.Internal;

/// <summary>One-time wiring of the engine-agnostic core to Godot: logger sinks and log levels from project settings.</summary>
internal static class NetfoxRuntime
{
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;

        NetfoxLogger.Print = s => GD.Print(s);
        NetfoxLogger.PushWarning = s => GD.PushWarning(s);
        NetfoxLogger.PushError = s => GD.PushError(s);

        NetfoxLogger.Level = (LogLevel)Settings.GetInt("netfox/logging/log_level", (int)NetfoxLogger.DefaultLogLevel);
        NetfoxLogger.ModuleLevels["netfox"] = (LogLevel)Settings.GetInt("netfox/logging/netfox_log_level", (int)NetfoxLogger.DefaultLogLevel);
        NetfoxLogger.ModuleLevels["netfox.extras"] = (LogLevel)Settings.GetInt("netfox/logging/netfox_extras_log_level", (int)NetfoxLogger.DefaultLogLevel);

        Snapshot.ValueComparer = VariantComparer.Instance;
    }
}

/// <summary>Value equality for Variants, approximating GDScript == : by contained value, recursively for collections.</summary>
internal sealed class VariantComparer : IEqualityComparer<Variant>
{
    public static readonly VariantComparer Instance = new();

    public bool Equals(Variant x, Variant y)
    {
        if (x.VariantType != y.VariantType) return false;

        switch (x.VariantType)
        {
            case Variant.Type.Nil:
                return true;
            case Variant.Type.Array:
                return x.AsGodotArray().RecursiveEqual(y.AsGodotArray());
            case Variant.Type.Dictionary:
                return x.AsGodotDictionary().RecursiveEqual(y.AsGodotDictionary());
            case Variant.Type.Object:
                return ReferenceEquals(x.AsGodotObject(), y.AsGodotObject());
        }

        var a = x.Obj;
        var b = y.Obj;
        if (a is Array arrayA && b is Array arrayB)
            return StructuralComparisons.StructuralEqualityComparer.Equals(arrayA, arrayB);
        return object.Equals(a, b);
    }

    public int GetHashCode(Variant obj) => obj.VariantType.GetHashCode();
}
