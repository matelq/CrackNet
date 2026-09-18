using CrackNet.Core.Serialization;
using Godot;

namespace CrackNet;

/// <summary>
/// Synced values on the wire: a type byte, then the value at float precision. <c>GD.VarToBytes</c> costs a 4-byte
/// header per value on top of doubles; a Vector3 goes from 20 bytes to 13. Types without a case here fall back to it.
/// </summary>
public static class CompactValues
{
    private const byte Fallback = 0, Bool = 1, Int = 2, Float = 3, Vector2 = 4, Vector3 = 5, Quaternion = 6, Transform3D = 7;

    public static void Encode(Variant value, ByteWriter writer)
    {
        switch (value.VariantType)
        {
            case Variant.Type.Bool:
                writer.PutU8(value.AsBool() ? (byte)(Bool | 0x80) : Bool);
                break;
            case Variant.Type.Int:
                writer.PutU8(Int);
                var number = value.AsInt64();
                VarUint.Encode((ulong)((number << 1) ^ (number >> 63)), writer);
                break;
            case Variant.Type.Float:
                writer.PutU8(Float);
                writer.PutFloat(value.AsSingle());
                break;
            case Variant.Type.Vector2:
                writer.PutU8(Vector2);
                PutVector2(value.AsVector2(), writer);
                break;
            case Variant.Type.Vector3:
                writer.PutU8(Vector3);
                PutVector3(value.AsVector3(), writer);
                break;
            case Variant.Type.Quaternion:
                var q = value.AsQuaternion();
                writer.PutU8(Quaternion);
                writer.PutFloat(q.X);
                writer.PutFloat(q.Y);
                writer.PutFloat(q.Z);
                writer.PutFloat(q.W);
                break;
            case Variant.Type.Transform3D:
                var t = value.AsTransform3D();
                writer.PutU8(Transform3D);
                PutVector3(t.Basis.Column0, writer);
                PutVector3(t.Basis.Column1, writer);
                PutVector3(t.Basis.Column2, writer);
                PutVector3(t.Origin, writer);
                break;
            default:
                writer.PutU8(Fallback);
                var bytes = GD.VarToBytes(value);
                VarUint.Encode(bytes.Length, writer);
                writer.PutData(bytes);
                break;
        }
    }

    public static Variant Decode(ByteReader reader)
    {
        var type = reader.GetU8();
        switch (type & 0x7F)
        {
            case Bool: return (type & 0x80) != 0;
            case Int:
                var zigzag = VarUint.Decode(reader);
                return (long)(zigzag >> 1) ^ -(long)(zigzag & 1);
            case Float: return reader.GetFloat();
            case Vector2: return GetVector2(reader);
            case Vector3: return GetVector3(reader);
            case Quaternion: return new Godot.Quaternion(reader.GetFloat(), reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            case Transform3D:
                var basis = new Basis(GetVector3(reader), GetVector3(reader), GetVector3(reader));
                return new Godot.Transform3D(basis, GetVector3(reader));
            default:
                var length = VarUint.DecodeInt(reader);
                return GD.BytesToVar(reader.GetData(length).ToArray());
        }
    }

    private static void PutVector2(Godot.Vector2 v, ByteWriter writer)
    {
        writer.PutFloat(v.X);
        writer.PutFloat(v.Y);
    }

    private static void PutVector3(Godot.Vector3 v, ByteWriter writer)
    {
        writer.PutFloat(v.X);
        writer.PutFloat(v.Y);
        writer.PutFloat(v.Z);
    }

    private static Godot.Vector2 GetVector2(ByteReader reader) => new(reader.GetFloat(), reader.GetFloat());
    private static Godot.Vector3 GetVector3(ByteReader reader) => new(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
}
