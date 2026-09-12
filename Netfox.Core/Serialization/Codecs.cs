using System.Text;
using Netfox.Core.Collections;
using Netfox.Core.Data;

namespace Netfox.Core.Serialization;

/// <summary>Variable-length unsigned integer: 7 data bits per byte, high bit marks continuation. Port of _VaruintSerializer.</summary>
public static class VarUint
{
    public static void Encode(ulong value, ByteWriter buffer)
    {
        for (var i = 0; i < 10; i++)
        {
            var nominator = (byte)(value & 0x7F);
            var continues = value > 0x7F;
            buffer.PutU8(continues ? (byte)(nominator | 0x80) : nominator);
            value >>= 7;
            if (!continues) break;
        }
    }

    public static ulong Decode(ByteReader buffer)
    {
        ulong value = 0;
        for (var i = 0; i < 10; i++)
        {
            var b = buffer.GetU8();
            value += (ulong)(b & 0x7F) << (i * 7);
            if ((b & 0x80) == 0) break;
        }
        return value;
    }

    public static void Encode(int value, ByteWriter buffer) => Encode((ulong)value, buffer);
    public static int DecodeInt(ByteReader buffer) => (int)Decode(buffer);
}

/// <summary>Variable-length bitset: 7 bits per byte, high bit marks continuation. Decoded bit count is a multiple of 7. Port of _VariableBitsetSerializer.</summary>
public static class VarBits
{
    public static void Encode(Bitset bitset, ByteWriter buffer)
    {
        if (bitset.IsEmpty)
        {
            buffer.PutU8(0);
            return;
        }

        for (var i = 0; i < bitset.BitCount; i += 7)
        {
            byte b = 0;
            for (var bit = 0; bit < 7; bit++)
                if (bitset.BitCount > i + bit && bitset.GetBit(i + bit)) b |= (byte)(1 << bit);
            if (bitset.BitCount > i + 7) b |= 0x80;
            buffer.PutU8(b);
        }
    }

    public static Bitset Decode(ByteReader buffer)
    {
        var bools = new List<bool>();
        while (true)
        {
            var b = buffer.GetU8();
            for (var bit = 0; bit < 7; bit++)
                bools.Add((b & (1 << bit)) != 0);
            if ((b & 0x80) == 0) break;
        }
        return Bitset.OfBools(bools);
    }
}

/// <summary>Zero-terminated UTF-8 string. Port of _CStringSerializer.</summary>
public static class CString
{
    public static void Encode(string value, ByteWriter buffer)
    {
        buffer.PutData(Encoding.UTF8.GetBytes(value));
        buffer.PutU8(0);
    }

    public static string Decode(ByteReader buffer)
    {
        var bytes = new List<byte>();
        while (buffer.AvailableBytes > 0)
        {
            var c = buffer.GetU8();
            if (c == 0) break;
            bytes.Add(c);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}

/// <summary>Encodes a NetworkIdentityReference as varuint id, or 0 followed by a c-string name. Port of _NetworkIdentityReferenceSerializer.</summary>
public static class NetRef
{
    public static void Encode(NetworkIdentityReference reference, ByteWriter buffer)
    {
        if (reference.HasId)
        {
            VarUint.Encode(reference.Id, buffer);
        }
        else
        {
            buffer.PutU8(0);
            CString.Encode(reference.FullName, buffer);
        }
    }

    public static NetworkIdentityReference Decode(ByteReader buffer)
    {
        var id = VarUint.DecodeInt(buffer);
        return id == 0
            ? NetworkIdentityReference.OfFullName(CString.Decode(buffer))
            : NetworkIdentityReference.OfId(id);
    }
}
