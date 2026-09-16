using System.Buffers.Binary;
using System.Text;

namespace Netfox.Core.Serialization;

/// <summary>Little-endian reader over a byte span. Replaces the read side of StreamPeerBuffer. Throws EndOfStreamException on underflow.</summary>
public sealed class ByteReader
{
    private readonly ReadOnlyMemory<byte> _data;
    private int _position;

    public ByteReader(ReadOnlyMemory<byte> data)
    {
        _data = data;
    }

    public ByteReader(byte[] data) : this(data.AsMemory()) { }

    public int Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _data.Length);
    }

    public int Length => _data.Length;
    public int AvailableBytes => _data.Length - _position;

    public byte GetU8() => Take(1)[0];
    public sbyte GetI8() => (sbyte)GetU8();
    public ushort GetU16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public short GetI16() => (short)GetU16();
    public uint GetU32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int GetI32() => (int)GetU32();
    public ulong GetU64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public long GetI64() => (long)GetU64();
    public Half GetHalf() => BitConverter.UInt16BitsToHalf(GetU16());
    public float GetFloat() => BitConverter.UInt32BitsToSingle(GetU32());
    public double GetDouble() => BitConverter.UInt64BitsToDouble(GetU64());

    /// <summary>Reads up to <paramref name="count"/> bytes; returns fewer if the buffer ends early, like get_partial_data.</summary>
    public ReadOnlyMemory<byte> GetPartialData(int count)
    {
        var n = Math.Min(count, AvailableBytes);
        var slice = _data.Slice(_position, n);
        _position += n;
        return slice;
    }

    public ReadOnlySpan<byte> GetData(int count) => Take(count);

    public string GetUtf8String()
    {
        var length = (int)GetU32();
        return Encoding.UTF8.GetString(Take(length));
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        // A negative count is a length that overflowed on decode: as out of data as one too large
        if (count < 0 || AvailableBytes < count)
            throw new EndOfStreamException($"Tried to read {count} bytes with {AvailableBytes} available");
        var span = _data.Span.Slice(_position, count);
        _position += count;
        return span;
    }
}
