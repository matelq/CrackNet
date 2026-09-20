using System.Collections;
using CrackNet.Core.Logging;
using Godot;

namespace CrackNet.Internal;

/// <summary>One-time wiring of the engine-agnostic core to Godot: logger sinks and log levels from project settings.</summary>
internal static class CrackNetRuntime
{
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;

        CrackNetLogger.Print = s => GD.Print(s);
        CrackNetLogger.PushWarning = s => GD.PushWarning(s);
        CrackNetLogger.PushError = s => GD.PushError(s);

        CrackNetLogger.ModuleLevels["cracknet"] = CrackNetSettings.Instance.CrackNetLogLevel;
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
