using Netfox.Core.Collections;
using Netfox.Core.Data;
using Netfox.Core.Serialization;

namespace Netfox.Core.Tests.Serialization;

public class CodecTests
{
    [Theory]
    [InlineData(7, 1)]
    [InlineData(14, 2)]
    [InlineData(21, 3)]
    [InlineData(28, 4)]
    [InlineData(35, 5)]
    [InlineData(42, 6)]
    [InlineData(49, 7)]
    [InlineData(56, 8)]
    [InlineData(63, 9)]
    public void VarUint_ShouldRoundtripMaxValueInNBytes(int bits, int expectedSize)
    {
        var value = (1UL << bits) - 1;
        var writer = new ByteWriter();
        VarUint.Encode(value, writer);
        Assert.Equal(expectedSize, writer.Size);
        Assert.Equal(value, VarUint.Decode(new ByteReader(writer.ToArray())));
    }

    [Fact]
    public void VarUint_ShouldEncodeZeroAsSingleByte()
    {
        var writer = new ByteWriter();
        VarUint.Encode(0, writer);
        Assert.Equal(new byte[] { 0 }, writer.ToArray());
    }

    [Fact]
    public void VarBits_ShouldRoundtripSevenBits()
    {
        var bits = Bitset.OfBools([false, true, false, true, true, false, true]);
        var writer = new ByteWriter();
        VarBits.Encode(bits, writer);
        Assert.Equal(1, writer.Size);
        Assert.Equal(bits, VarBits.Decode(new ByteReader(writer.ToArray())));
    }

    [Fact]
    public void VarBits_ShouldRoundtripFourteenBits()
    {
        var bits = Bitset.OfBools([false, true, false, true, true, false, true, false, true, true, true, false, false, true]);
        var writer = new ByteWriter();
        VarBits.Encode(bits, writer);
        Assert.Equal(2, writer.Size);
        Assert.Equal(bits, VarBits.Decode(new ByteReader(writer.ToArray())));
    }

    [Fact]
    public void VarBits_ShouldPadToMultipleOfSeven()
    {
        var bits = Bitset.OfBools([true, false, true]);
        var writer = new ByteWriter();
        VarBits.Encode(bits, writer);
        var decoded = VarBits.Decode(new ByteReader(writer.ToArray()));
        Assert.Equal(7, decoded.BitCount);
        Assert.Equal([0, 2], decoded.GetSetIndices());
    }

    [Theory]
    [InlineData(12, 1)]
    [InlineData(138, 2)]
    public void NetRef_ShouldRoundtripId(int id, int expectedSize)
    {
        var reference = NetworkIdentityReference.OfId(id);
        var writer = new ByteWriter();
        NetRef.Encode(reference, writer);
        Assert.Equal(expectedSize, writer.Size);
        Assert.Equal(reference, NetRef.Decode(new ByteReader(writer.ToArray())));
    }

    [Fact]
    public void NetRef_ShouldRoundtripFullName()
    {
        var reference = NetworkIdentityReference.OfFullName("path");
        var writer = new ByteWriter();
        NetRef.Encode(reference, writer);
        Assert.Equal(6, writer.Size);
        Assert.Equal(reference, NetRef.Decode(new ByteReader(writer.ToArray())));
    }

    [Fact]
    public void CString_ShouldRoundtripAndStopAtZero()
    {
        var writer = new ByteWriter();
        CString.Encode("hi!!", writer);
        writer.PutU8(42);
        Assert.Equal(6, writer.Size);
        var reader = new ByteReader(writer.ToArray());
        Assert.Equal("hi!!", CString.Decode(reader));
        Assert.Equal(42, reader.GetU8());
    }

    [Fact]
    public void ByteBuffers_ShouldRoundtripPrimitivesLittleEndian()
    {
        var writer = new ByteWriter(2);
        writer.PutU8(0xAB);
        writer.PutI8(-3);
        writer.PutU16(0x1234);
        writer.PutI16(-1234);
        writer.PutU32(0xDEADBEEF);
        writer.PutI32(-70000);
        writer.PutU64(ulong.MaxValue - 5);
        writer.PutI64(long.MinValue + 7);
        writer.PutHalf((Half)2.0);
        writer.PutFloat(1.5f);
        writer.PutDouble(Math.PI);
        writer.PutUtf8String("hi!!");
        writer.PutData([1, 2, 3]);

        var bytes = writer.ToArray();
        Assert.Equal(0x34, bytes[2]); // low byte first
        Assert.Equal(0x12, bytes[3]);

        var reader = new ByteReader(bytes);
        Assert.Equal(0xAB, reader.GetU8());
        Assert.Equal(-3, reader.GetI8());
        Assert.Equal(0x1234, reader.GetU16());
        Assert.Equal(-1234, reader.GetI16());
        Assert.Equal(0xDEADBEEF, reader.GetU32());
        Assert.Equal(-70000, reader.GetI32());
        Assert.Equal(ulong.MaxValue - 5, reader.GetU64());
        Assert.Equal(long.MinValue + 7, reader.GetI64());
        Assert.Equal((Half)2.0, reader.GetHalf());
        Assert.Equal(1.5f, reader.GetFloat());
        Assert.Equal(Math.PI, reader.GetDouble());
        Assert.Equal("hi!!", reader.GetUtf8String());
        Assert.Equal(new byte[] { 1, 2, 3 }, reader.GetData(3).ToArray());
        Assert.Equal(0, reader.AvailableBytes);
        Assert.Throws<EndOfStreamException>(() => reader.GetU8());
    }

    [Fact]
    public void Utf8String_ShouldUseFourByteLengthPrefix()
    {
        var writer = new ByteWriter();
        writer.PutUtf8String("hi!!");
        Assert.Equal(8, writer.Size);
    }

    [Fact]
    public void GetPartialData_ShouldTruncateAtEnd()
    {
        var reader = new ByteReader(new byte[] { 1, 2, 3 });
        Assert.Equal(3, reader.GetPartialData(10).Length);
        Assert.Equal(0, reader.AvailableBytes);
    }
}
