using Netfox.Core.Serialization;

namespace Netfox.Core.Tests.Serialization;

public class PacketBufferTests
{
    private static byte[] Chunk(int size)
    {
        var chunk = new byte[size];
        Array.Fill(chunk, (byte)0xEF);
        return chunk;
    }

    [Fact]
    public void ShouldReturnSingle()
    {
        var buffer = new PacketBuffer(16);
        buffer.Push(Chunk(15));
        var packets = buffer.Finish();
        Assert.Single(packets);
        Assert.Equal(15, packets[0].Length);
    }

    [Fact]
    public void ShouldReturnNone() => Assert.Empty(new PacketBuffer().Finish());

    [Fact]
    public void ShouldReturnMultiple()
    {
        var buffer = new PacketBuffer(16);
        buffer.Push(Chunk(15));
        buffer.Push(Chunk(6));
        var packets = buffer.Finish();
        Assert.Equal(2, packets.Count);
        Assert.Equal(15, packets[0].Length);
        Assert.Equal(6, packets[1].Length);
    }

    [Fact]
    public void ShouldKeepOversizedChunk()
    {
        var buffer = new PacketBuffer(16);
        buffer.Push(Chunk(24));
        buffer.Push(Chunk(6));
        var packets = buffer.Finish();
        Assert.Equal(2, packets.Count);
        Assert.Equal(24, packets[0].Length);
        Assert.Equal(6, packets[1].Length);
    }

    [Fact]
    public void ShouldBeDisabledWithZeroLimit()
    {
        var buffer = new PacketBuffer(0);
        buffer.Push(Chunk(1024));
        buffer.Push(Chunk(2048));
        var packets = buffer.Finish();
        Assert.Single(packets);
        Assert.Equal(3072, packets[0].Length);
    }

    [Fact]
    public void ShouldRunPacketSetup()
    {
        var setups = 0;
        var buffer = new PacketBuffer { PacketSetup = w => { setups++; w.PutU32(7); } };
        buffer.Push(Chunk(16));
        var packets = buffer.Finish();
        Assert.Equal(1, setups);
        Assert.Equal(20, packets[0].Length);
    }
}
