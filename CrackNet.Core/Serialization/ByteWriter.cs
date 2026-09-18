using System.Buffers.Binary;
using System.Text;

namespace CrackNet.Core.Serialization;

/// <summary>Growable little-endian byte buffer. Replaces the write side of StreamPeerBuffer.</summary>
public sealed class ByteWriter
{
    private byte[] _buffer;
    private int _position;

    public ByteWriter(int initialCapacity = 64)
    {
        _buffer = new byte[Math.Max(initialCapacity, 8)];
    }

    public int Size => _position;
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _position);

    /// <summary>What has been written, without copying it. Only valid until the next write, which may reallocate.</summary>
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _position);

    public byte[] ToArray() => WrittenSpan.ToArray();

    public void Clear() => _position = 0;

    public void PutU8(byte value)
    {
        Ensure(1);
        _buffer[_position++] = value;
    }

    public void PutI8(sbyte value) => PutU8((byte)value);

    public void PutU16(ushort value)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_position), value);
        _position += 2;
    }

    public void PutI16(short value) => PutU16((ushort)value);

    public void PutU32(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_position), value);
        _position += 4;
    }

    public void PutI32(int value) => PutU32((uint)value);

    public void PutU64(ulong value)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_position), value);
        _position += 8;
    }

    public void PutI64(long value) => PutU64((ulong)value);

    public void PutHalf(Half value) => PutU16(BitConverter.HalfToUInt16Bits(value));

    public void PutFloat(float value) => PutU32(BitConverter.SingleToUInt32Bits(value));

    public void PutDouble(double value) => PutU64(BitConverter.DoubleToUInt64Bits(value));

    public void PutData(ReadOnlySpan<byte> data)
    {
        Ensure(data.Length);
        data.CopyTo(_buffer.AsSpan(_position));
        _position += data.Length;
    }

    /// <summary>u32 byte length followed by UTF-8 bytes, matching StreamPeer.put_utf8_string.</summary>
    public void PutUtf8String(string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        PutU32((uint)byteCount);
        Ensure(byteCount);
        Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_position));
        _position += byteCount;
    }

    private void Ensure(int count)
    {
        if (_position + count <= _buffer.Length) return;
        Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _position + count));
    }
}
