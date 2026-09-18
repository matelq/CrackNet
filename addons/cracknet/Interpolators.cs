using Godot;

namespace CrackNet;

/// <summary>
/// Registry of value interpolators by type. Discrete types (bool, integers, integer vectors, strings, references) have
/// none on purpose: they step.
/// </summary>
public static class Interpolators
{
    public sealed class Interpolator
    {
        public Func<Variant, bool> IsApplicable { get; }
        public Func<Variant, Variant, double, Variant> Apply { get; }

        public Interpolator(Func<Variant, bool> isApplicable, Func<Variant, Variant, double, Variant> apply)
        {
            IsApplicable = isApplicable;
            Apply = apply;
        }
    }

    /// <summary>Fallback: snap to whichever endpoint is closer.</summary>
    public static Func<Variant, Variant, double, Variant> DefaultApply { get; set; } = (a, b, f) => f < 0.5 ? a : b;

    public static readonly Interpolator DefaultInterpolator = new(_ => true, (a, b, f) => DefaultApply(a, b, f));

    private static readonly List<Interpolator> Registered = new();

    /// <summary>Registered interpolators take precedence over earlier ones.</summary>
    public static void Register(Func<Variant, bool> isApplicable, Func<Variant, Variant, double, Variant> apply)
        => Registered.Insert(0, new Interpolator(isApplicable, apply));

    public static Func<Variant, Variant, double, Variant> FindFor(Variant value) => FindInterpolatorFor(value).Apply;

    public static Interpolator FindInterpolatorFor(Variant value)
    {
        foreach (var interpolator in Registered)
            if (interpolator.IsApplicable(value)) return interpolator;
        return DefaultInterpolator;
    }

    public static Variant Interpolate(Variant a, Variant b, double f) => FindFor(a)(a, b, f);

    static Interpolators()
    {
        RegisterType(Variant.Type.Float, (a, b, f) => Mathf.Lerp(a.AsDouble(), b.AsDouble(), f));

        RegisterType(Variant.Type.Vector2, (a, b, f) => a.AsVector2().Lerp(b.AsVector2(), (float)f));
        RegisterType(Variant.Type.Vector3, (a, b, f) => a.AsVector3().Lerp(b.AsVector3(), (float)f));
        RegisterType(Variant.Type.Vector4, (a, b, f) => a.AsVector4().Lerp(b.AsVector4(), (float)f));

        RegisterType(Variant.Type.Transform2D, (a, b, f) => a.AsTransform2D().InterpolateWith(b.AsTransform2D(), (float)f));
        RegisterType(Variant.Type.Transform3D, (a, b, f) => a.AsTransform3D().InterpolateWith(b.AsTransform3D(), (float)f));

        RegisterType(Variant.Type.Quaternion, (a, b, f) => a.AsQuaternion().Slerp(b.AsQuaternion(), (float)f));
        RegisterType(Variant.Type.Basis, (a, b, f) => a.AsBasis().Slerp(b.AsBasis(), (float)f));
        RegisterType(Variant.Type.Color, (a, b, f) => a.AsColor().Lerp(b.AsColor(), (float)f));
    }

    private static void RegisterType(Variant.Type type, Func<Variant, Variant, double, Variant> apply)
        => Register(v => v.VariantType == type, apply);
}
