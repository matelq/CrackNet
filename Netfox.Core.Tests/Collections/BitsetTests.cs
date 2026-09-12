using Netfox.Core.Collections;

namespace Netfox.Core.Tests.Collections;

public class BitsetTests
{
    [Fact]
    public void ShouldBeEmptyOnCreate()
    {
        var bits = new Bitset(2);
        Assert.False(bits.GetBit(0));
        Assert.False(bits.GetBit(1));
    }

    [Fact]
    public void GetSetIndices() => Assert.Equal([1, 2], Bitset.OfBools([false, true, true, false]).GetSetIndices());

    [Fact]
    public void SetBit()
    {
        var bits = new Bitset(4);
        bits.SetBit(1);
        bits.SetBit(3);
        Assert.Equal(Bitset.OfBools([false, true, false, true]), bits);
    }

    [Fact]
    public void ClearBit()
    {
        var bits = Bitset.OfBools([false, true, true, false]);
        bits.ClearBit(1);
        bits.ClearBit(3);
        Assert.Equal(Bitset.OfBools([false, false, true, false]), bits);
    }

    [Fact]
    public void ToggleBit()
    {
        var bits = Bitset.OfBools([false, true, true, false]);
        bits.ToggleBit(0);
        bits.ToggleBit(1);
        Assert.Equal(Bitset.OfBools([true, false, true, false]), bits);
    }

    [Fact]
    public void ShouldSpanMultipleBytes()
    {
        var bits = new Bitset(12);
        bits.SetBit(9);
        Assert.Equal(2, bits.Bytes.Length);
        Assert.Equal([9], bits.GetSetIndices());
        Assert.Throws<ArgumentOutOfRangeException>(() => bits.GetBit(12));
    }
}
