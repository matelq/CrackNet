using Godot;
using Netfox.Core.Serialization;

namespace Netfox;

/// <summary>
/// Base class for schema serializers. Encode a Variant into a ByteWriter, decode it back from a ByteReader.
/// Extend to implement custom serializers and pass them to RollbackSynchronizer.SetSchema. Port of schemas/network-schema-serializer.gd.
/// </summary>
public abstract class NetworkSchemaSerializer
{
    public abstract void Encode(Variant value, ByteWriter buffer);
    public abstract Variant Decode(ByteReader buffer);

    /// <summary>
    /// The value as it will come back out the other end. For a lossy schema this is not the value that went in, and
    /// that difference is the point: a peer recording what it actually has and every other peer recording what it was
    /// sent are then simulating from two different numbers for the same tick.
    /// <para>
    /// The error is tiny - half precision is about 5e-4 relative - but it is systematic rather than noise, so it
    /// never averages out and produces a steady trickle of corrections no amount of bandwidth removes.
    /// <a href="https://gafferongames.com/post/state_synchronization/">State Synchronization</a> prescribes exactly
    /// this: quantize the simulation as if it had been sent, on both sides.
    /// </para>
    /// <para>
    /// The default round trips through <see cref="Encode"/> and <see cref="Decode"/>, so a custom serializer is
    /// correct without doing anything. Override it where the answer is cheaper to compute directly, or where the
    /// encoding is lossless and the whole round trip can be skipped.
    /// </para>
    /// </summary>
    public virtual Variant Quantize(Variant value)
    {
        var buffer = _quantizeBuffer ??= new ByteWriter();
        buffer.Clear();
        Encode(value, buffer);
        return Decode(new ByteReader(buffer.WrittenMemory));
    }

    /// <summary>
    /// Reused across calls, because this runs on the record path for every schema'd property every tick. Per thread
    /// rather than shared: nothing here is synchronized, and a second thread encoding into the same buffer would
    /// corrupt both answers rather than merely slow them down.
    /// </summary>
    [ThreadStatic] private static ByteWriter? _quantizeBuffer;
}

/// <summary>
/// Factory of schema serializers. Port of schemas/network-schemas.gd; naming follows the original (uint16, vec3f32, ...).
/// <para>
/// <see cref="Variant"/> and <see cref="String"/> read like the types of the same name on purpose: they are kept as
/// upstream names them, so the upstream schema documentation applies here unchanged. C# resolves the two without
/// ambiguity, and renaming them would cost that mapping for a cosmetic gain.
/// </para>
/// </summary>
public static class NetworkSchemas
{
    /// <summary>Any type supported by GD.VarToBytes; size depends on the value.</summary>
    public static NetworkSchemaSerializer Variant() => VariantSerializer.Instance;
    /// <summary>UTF-8 string prefixed by a 32-bit length.</summary>
    public static NetworkSchemaSerializer String() => StringSerializer.Instance;
    /// <summary>UTF-8 string terminated by a zero byte.</summary>
    public static NetworkSchemaSerializer CString() => CStringSerializer.Instance;
    public static NetworkSchemaSerializer Bool8() => Bool8Serializer.Instance;

    public static NetworkSchemaSerializer Uint8() => IntSerializer.U8;
    public static NetworkSchemaSerializer Uint16() => IntSerializer.U16;
    public static NetworkSchemaSerializer Uint32() => IntSerializer.U32;
    public static NetworkSchemaSerializer Uint64() => IntSerializer.U64;
    /// <summary>Variable-length unsigned integer, 1 to 10 bytes.</summary>
    public static NetworkSchemaSerializer Varuint() => VaruintSerializer.Instance;

    public static NetworkSchemaSerializer Int8() => IntSerializer.I8;
    public static NetworkSchemaSerializer Int16() => IntSerializer.I16;
    public static NetworkSchemaSerializer Int32() => IntSerializer.I32;
    public static NetworkSchemaSerializer Int64() => IntSerializer.I64;

    public static NetworkSchemaSerializer Float16() => FloatSerializer.F16;
    public static NetworkSchemaSerializer Float32() => FloatSerializer.F32;
    public static NetworkSchemaSerializer Float64() => FloatSerializer.F64;

    /// <summary>Signed fraction in [-1, 1] quantized to 8 bits.</summary>
    public static NetworkSchemaSerializer Sfrac8() => new QuantizingSerializer(Uint8(), -1.0, 1.0, 0, 0xFF);
    public static NetworkSchemaSerializer Sfrac16() => new QuantizingSerializer(Uint16(), -1.0, 1.0, 0, 0xFFFF);
    public static NetworkSchemaSerializer Sfrac32() => new QuantizingSerializer(Uint32(), -1.0, 1.0, 0, 0xFFFFFFFF);

    /// <summary>Unsigned fraction in [0, 1] quantized to 8 bits.</summary>
    public static NetworkSchemaSerializer Ufrac8() => new QuantizingSerializer(Uint8(), 0.0, 1.0, 0, 0xFF);
    public static NetworkSchemaSerializer Ufrac16() => new QuantizingSerializer(Uint16(), 0.0, 1.0, 0, 0xFFFF);
    public static NetworkSchemaSerializer Ufrac32() => new QuantizingSerializer(Uint32(), 0.0, 1.0, 0, 0xFFFFFFFF);

    /// <summary>Angle in degrees, wrapped to [0, 360) and quantized to 8 bits.</summary>
    public static NetworkSchemaSerializer Degrees8() => new ModuloSerializer(Uint8(), 360.0, 0xFF);
    public static NetworkSchemaSerializer Degrees16() => new ModuloSerializer(Uint16(), 360.0, 0xFFFF);
    public static NetworkSchemaSerializer Degrees32() => new ModuloSerializer(Uint32(), 360.0, 0xFFFFFFFF);

    /// <summary>Angle in radians, wrapped to [0, TAU) and quantized to 8 bits.</summary>
    public static NetworkSchemaSerializer Radians8() => new ModuloSerializer(Uint8(), Math.Tau, 0xFF);
    public static NetworkSchemaSerializer Radians16() => new ModuloSerializer(Uint16(), Math.Tau, 0xFFFF);
    public static NetworkSchemaSerializer Radians32() => new ModuloSerializer(Uint32(), Math.Tau, 0xFFFFFFFF);

    public static NetworkSchemaSerializer Vec2T(NetworkSchemaSerializer component) => new Vec2Serializer(component);
    public static NetworkSchemaSerializer Vec2F16() => Vec2T(Float16());
    public static NetworkSchemaSerializer Vec2F32() => Vec2T(Float32());
    public static NetworkSchemaSerializer Vec2F64() => Vec2T(Float64());

    public static NetworkSchemaSerializer Vec3T(NetworkSchemaSerializer component) => new Vec3Serializer(component);
    public static NetworkSchemaSerializer Vec3F16() => Vec3T(Float16());
    public static NetworkSchemaSerializer Vec3F32() => Vec3T(Float32());
    public static NetworkSchemaSerializer Vec3F64() => Vec3T(Float64());

    public static NetworkSchemaSerializer Vec4T(NetworkSchemaSerializer component) => new Vec4Serializer(component);
    public static NetworkSchemaSerializer Vec4F16() => Vec4T(Float16());
    public static NetworkSchemaSerializer Vec4F32() => Vec4T(Float32());
    public static NetworkSchemaSerializer Vec4F64() => Vec4T(Float64());

    /// <summary>Unit Vector2 as a single angle.</summary>
    public static NetworkSchemaSerializer Normal2T(NetworkSchemaSerializer component) => new Normal2Serializer(component);
    public static NetworkSchemaSerializer Normal2F16() => Normal2T(Float16());
    public static NetworkSchemaSerializer Normal2F32() => Normal2T(Float32());
    public static NetworkSchemaSerializer Normal2F64() => Normal2T(Float64());

    /// <summary>Unit Vector3 as two octahedron-encoded components.</summary>
    public static NetworkSchemaSerializer Normal3T(NetworkSchemaSerializer component) => new Normal3Serializer(component);
    public static NetworkSchemaSerializer Normal3F16() => Normal3T(Float16());
    public static NetworkSchemaSerializer Normal3F32() => Normal3T(Float32());
    public static NetworkSchemaSerializer Normal3F64() => Normal3T(Float64());

    public static NetworkSchemaSerializer QuatT(NetworkSchemaSerializer component) => new QuaternionSerializer(component);
    public static NetworkSchemaSerializer QuatF16() => QuatT(Float16());
    public static NetworkSchemaSerializer QuatF32() => QuatT(Float32());
    public static NetworkSchemaSerializer QuatF64() => QuatT(Float64());

    public static NetworkSchemaSerializer Transform2T(NetworkSchemaSerializer component) => new Transform2DSerializer(component);
    public static NetworkSchemaSerializer Transform2F16() => Transform2T(Float16());
    public static NetworkSchemaSerializer Transform2F32() => Transform2T(Float32());
    public static NetworkSchemaSerializer Transform2F64() => Transform2T(Float64());

    public static NetworkSchemaSerializer Transform3T(NetworkSchemaSerializer component) => new Transform3DSerializer(component);
    public static NetworkSchemaSerializer Transform3F16() => Transform3T(Float16());
    public static NetworkSchemaSerializer Transform3F32() => Transform3T(Float32());
    public static NetworkSchemaSerializer Transform3F64() => Transform3T(Float64());

    /// <summary>Godot Array with a size prefix, each item with <paramref name="item"/>.</summary>
    public static NetworkSchemaSerializer ArrayOf(NetworkSchemaSerializer? item = null, NetworkSchemaSerializer? size = null)
        => new ArraySerializer(item ?? Variant(), size ?? Uint16());

    /// <summary>Godot Dictionary with a size prefix.</summary>
    public static NetworkSchemaSerializer Dictionary(NetworkSchemaSerializer? key = null, NetworkSchemaSerializer? value = null, NetworkSchemaSerializer? size = null)
        => new DictionarySerializer(key ?? Variant(), value ?? Variant(), size ?? Uint16());

    // Serializer classes

    private sealed class VariantSerializer : NetworkSchemaSerializer
    {
        public static readonly VariantSerializer Instance = new();

        // StreamPeer.put_var writes a 32-bit length followed by the encoded variant
        public override void Encode(Variant value, ByteWriter buffer)
        {
            var bytes = GD.VarToBytes(value);
            buffer.PutU32((uint)bytes.Length);
            buffer.PutData(bytes);
        }

        public override Variant Decode(ByteReader buffer)
        {
            var length = (int)buffer.GetU32();
            return GD.BytesToVar(buffer.GetData(length).ToArray());
        }
    }

    private sealed class StringSerializer : NetworkSchemaSerializer
    {
        public static readonly StringSerializer Instance = new();
        public override void Encode(Variant value, ByteWriter buffer) => buffer.PutUtf8String(value.AsString());
        public override Variant Decode(ByteReader buffer) => buffer.GetUtf8String();
    }

    private sealed class CStringSerializer : NetworkSchemaSerializer
    {
        public static readonly CStringSerializer Instance = new();
        public override void Encode(Variant value, ByteWriter buffer) => Core.Serialization.CString.Encode(value.AsString(), buffer);
        public override Variant Decode(ByteReader buffer) => Core.Serialization.CString.Decode(buffer);
    }

    private sealed class Bool8Serializer : NetworkSchemaSerializer
    {
        public static readonly Bool8Serializer Instance = new();
        public override void Encode(Variant value, ByteWriter buffer) => buffer.PutU8(value.AsBool() ? (byte)1 : (byte)0);
        public override Variant Decode(ByteReader buffer) => buffer.GetU8() > 0;
    }

    private sealed class IntSerializer : NetworkSchemaSerializer
    {
        public static readonly IntSerializer U8 = new((v, b) => b.PutU8((byte)v), b => b.GetU8());
        public static readonly IntSerializer U16 = new((v, b) => b.PutU16((ushort)v), b => b.GetU16());
        public static readonly IntSerializer U32 = new((v, b) => b.PutU32((uint)v), b => b.GetU32());
        public static readonly IntSerializer U64 = new((v, b) => b.PutU64((ulong)v), b => (long)b.GetU64());
        public static readonly IntSerializer I8 = new((v, b) => b.PutI8((sbyte)v), b => b.GetI8());
        public static readonly IntSerializer I16 = new((v, b) => b.PutI16((short)v), b => b.GetI16());
        public static readonly IntSerializer I32 = new((v, b) => b.PutI32((int)v), b => b.GetI32());
        public static readonly IntSerializer I64 = new((v, b) => b.PutI64(v), b => b.GetI64());

        private readonly Action<long, ByteWriter> _encode;
        private readonly Func<ByteReader, long> _decode;

        private IntSerializer(Action<long, ByteWriter> encode, Func<ByteReader, long> decode)
        {
            _encode = encode;
            _decode = decode;
        }

        // Floats truncate to int like GDScript put_u8(float) does
        public override void Encode(Variant value, ByteWriter buffer) => _encode(ToInt(value), buffer);
        public override Variant Decode(ByteReader buffer) => _decode(buffer);

        private static long ToInt(Variant value) => value.VariantType == Godot.Variant.Type.Float ? (long)value.AsDouble() : value.AsInt64();
    }

    private sealed class VaruintSerializer : NetworkSchemaSerializer
    {
        public static readonly VaruintSerializer Instance = new();
        public override void Encode(Variant value, ByteWriter buffer) => VarUint.Encode((ulong)value.AsInt64(), buffer);
        public override Variant Decode(ByteReader buffer) => (long)VarUint.Decode(buffer);
    }

    private sealed class FloatSerializer : NetworkSchemaSerializer
    {
        public static readonly FloatSerializer F16 = new((v, b) => b.PutHalf((Half)v), b => (double)b.GetHalf());
        public static readonly FloatSerializer F32 = new((v, b) => b.PutFloat((float)v), b => b.GetFloat());
        public static readonly FloatSerializer F64 = new((v, b) => b.PutDouble(v), b => b.GetDouble());

        private readonly Action<double, ByteWriter> _encode;
        private readonly Func<ByteReader, double> _decode;

        private FloatSerializer(Action<double, ByteWriter> encode, Func<ByteReader, double> decode)
        {
            _encode = encode;
            _decode = decode;
        }

        public override void Encode(Variant value, ByteWriter buffer) => _encode(value.AsDouble(), buffer);
        public override Variant Decode(ByteReader buffer) => _decode(buffer);
    }

    private sealed class Vec2Serializer(NetworkSchemaSerializer component) : NetworkSchemaSerializer
    {
        public override void Encode(Variant value, ByteWriter buffer)
        {
            var v = value.AsVector2();
            component.Encode(v.X, buffer);
            component.Encode(v.Y, buffer);
        }

        public override Variant Decode(ByteReader buffer)
            => new Vector2((float)component.Decode(buffer).AsDouble(), (float)component.Decode(buffer).AsDouble());
    }

    private sealed class Vec3Serializer(NetworkSchemaSerializer component) : NetworkSchemaSerializer
    {
        public override void Encode(Variant value, ByteWriter buffer)
        {
            var v = value.AsVector3();
            component.Encode(v.X, buffer);
            component.Encode(v.Y, buffer);
            component.Encode(v.Z, buffer);
        }

        public override Variant Decode(ByteReader buffer)
            => new Vector3((float)component.Decode(buffer).AsDouble(), (float)component.Decode(buffer).AsDouble(), (float)component.Decode(buffer).AsDouble());
    }

    private sealed class Vec4Serializer(NetworkSchemaSerializer component) : NetworkSchemaSerializer
    {
        public override void Encode(Variant value, ByteWriter buffer)
        {
            var v = value.AsVector4();
            component.Encode(v.X, buffer);
            component.Encode(v.Y, buffer);
            component.Encode(v.Z, buffer);
            component.Encode(v.W, buffer);
        }

        public override Variant Decode(ByteReader buffer)
            => new Vector4((float)component.Decode(buffer).AsDouble(), (float)component.Decode(buffer).AsDouble(), (float)component.Decode(buffer).AsDouble(), (float)component.Decode(buffer).AsDouble());
    }

    private sealed class Normal2Serializer(NetworkSchemaSerializer component) : NetworkSchemaSerializer
    {
        public override void Encode(Variant value, ByteWriter buffer) => component.Encode(value.AsVector2().Angle(), buffer);
        public override Variant Decode(ByteReader buffer) => Vector2.Right.Rotated((float)component.Decode(buffer).AsDouble());
    }

    private sealed class Normal3Serializer(NetworkSchemaSerializer component) : NetworkSchemaSerializer
    {
        public override void Encode(Variant value, ByteWriter buffer)
        {
            var uv = value.AsVector3().OctahedronEncode();
            component.Encode(uv.X, buffer);
            component.Encode(uv.Y, buffer);
        }

        public override Variant Decode(ByteReader buffer)
            => Vector3.OctahedronDecode(new Vector2((float)component.Decode(buffer).AsDouble(), (float)component.Decode(buffer).AsDouble()));
    }

    private sealed class QuaternionSerializer(NetworkSchemaSerializer component) : NetworkSchemaSerializer
    {
        public override void Encode(Variant value, ByteWriter buffer)
        {
            var q = value.AsQuaternion();
            component.Encode(q.X, buffer);
            component.Encode(q.Y, buffer);
            component.Encode(q.Z, buffer);
            component.Encode(q.W, buffer);
        }

        public override Variant Decode(ByteReader buffer)
            => new Quaternion((float)component.Decode(buffer).AsDouble(), (float)component.Decode(buffer).AsDouble(), (float)component.Decode(buffer).AsDouble(), (float)component.Decode(buffer).AsDouble());
    }

    private sealed class Transform2DSerializer(NetworkSchemaSerializer component) : NetworkSchemaSerializer
    {
        public override void Encode(Variant value, ByteWriter buffer)
        {
            var t = value.AsTransform2D();
            component.Encode(t.X.X, buffer); component.Encode(t.X.Y, buffer);
            component.Encode(t.Y.X, buffer); component.Encode(t.Y.Y, buffer);
            component.Encode(t.Origin.X, buffer); component.Encode(t.Origin.Y, buffer);
        }

        public override Variant Decode(ByteReader buffer)
        {
            float Next() => (float)component.Decode(buffer).AsDouble();
            return new Transform2D(new Vector2(Next(), Next()), new Vector2(Next(), Next()), new Vector2(Next(), Next()));
        }
    }

    private sealed class Transform3DSerializer(NetworkSchemaSerializer component) : NetworkSchemaSerializer
    {
        public override void Encode(Variant value, ByteWriter buffer)
        {
            var t = value.AsTransform3D();
            var b = t.Basis;
            component.Encode(b.X.X, buffer); component.Encode(b.X.Y, buffer); component.Encode(b.X.Z, buffer);
            component.Encode(b.Y.X, buffer); component.Encode(b.Y.Y, buffer); component.Encode(b.Y.Z, buffer);
            component.Encode(b.Z.X, buffer); component.Encode(b.Z.Y, buffer); component.Encode(b.Z.Z, buffer);
            component.Encode(t.Origin.X, buffer); component.Encode(t.Origin.Y, buffer); component.Encode(t.Origin.Z, buffer);
        }

        public override Variant Decode(ByteReader buffer)
        {
            float Next() => (float)component.Decode(buffer).AsDouble();
            var basis = new Basis(new Vector3(Next(), Next(), Next()), new Vector3(Next(), Next(), Next()), new Vector3(Next(), Next(), Next()));
            return new Transform3D(basis, new Vector3(Next(), Next(), Next()));
        }
    }

    private sealed class QuantizingSerializer(NetworkSchemaSerializer component, double fromMin, double fromMax, double toMin, double toMax) : NetworkSchemaSerializer
    {
        public override void Encode(Variant value, ByteWriter buffer)
        {
            var f = InverseLerp(fromMin, fromMax, value.AsDouble());
            var s = Lerp(toMin, toMax, f);
            component.Encode(s, buffer);
        }

        public override Variant Decode(ByteReader buffer)
        {
            var s = component.Decode(buffer).AsDouble();
            var f = InverseLerp(toMin, toMax, s);
            return Lerp(fromMin, fromMax, f);
        }
    }

    private sealed class ModuloSerializer(NetworkSchemaSerializer component, double valueMax, double componentMax) : NetworkSchemaSerializer
    {
        public override void Encode(Variant value, ByteWriter buffer)
        {
            var f = Mathf.PosMod(value.AsDouble(), valueMax) / valueMax;
            component.Encode(f * componentMax, buffer);
        }

        public override Variant Decode(ByteReader buffer)
        {
            var s = component.Decode(buffer).AsDouble();
            return s / componentMax * valueMax;
        }
    }

    private sealed class ArraySerializer(NetworkSchemaSerializer item, NetworkSchemaSerializer size) : NetworkSchemaSerializer
    {
        public override void Encode(Variant value, ByteWriter buffer)
        {
            var array = value.AsGodotArray();
            size.Encode(array.Count, buffer);
            foreach (var element in array)
                item.Encode(element, buffer);
        }

        public override Variant Decode(ByteReader buffer)
        {
            var count = (int)size.Decode(buffer).AsInt64();
            var array = new Godot.Collections.Array();
            array.Resize(count);
            for (var i = 0; i < count; i++)
                array[i] = item.Decode(buffer);
            return array;
        }
    }

    private sealed class DictionarySerializer(NetworkSchemaSerializer key, NetworkSchemaSerializer value, NetworkSchemaSerializer size) : NetworkSchemaSerializer
    {
        public override void Encode(Variant input, ByteWriter buffer)
        {
            var dictionary = input.AsGodotDictionary();
            size.Encode(dictionary.Count, buffer);
            foreach (var (k, v) in dictionary)
            {
                key.Encode(k, buffer);
                value.Encode(v, buffer);
            }
        }

        public override Variant Decode(ByteReader buffer)
        {
            var count = (int)size.Decode(buffer).AsInt64();
            var dictionary = new Godot.Collections.Dictionary();
            for (var i = 0; i < count; i++)
            {
                var k = key.Decode(buffer);
                var v = value.Decode(buffer);
                dictionary[k] = v;
            }
            return dictionary;
        }
    }

    private static double InverseLerp(double from, double to, double value) => (value - from) / (to - from);
    private static double Lerp(double from, double to, double weight) => from + (to - from) * weight;
}
