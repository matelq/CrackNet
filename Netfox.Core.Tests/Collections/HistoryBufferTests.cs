using Netfox.Core.Collections;

namespace Netfox.Core.Tests.Collections;

public class HistoryBufferTests
{
    private static HistoryBuffer<string> Filled()
        => HistoryBuffer<string>.Of(16, new Dictionary<int, string> { [2] = "foo", [4] = "bar", [8] = "baz" });

    [Fact]
    public void ShouldNotHaveLatestIfEmpty()
    {
        var empty = new HistoryBuffer<string>();
        Assert.False(empty.HasAt(16));
        Assert.False(empty.HasLatestAt(16));
    }

    [Fact]
    public void ShouldNotHaveLatestOutOfBounds() => Assert.False(Filled().HasLatestAt(0));

    [Fact]
    public void ShouldReturnSelfOnKnownItem()
    {
        var buffer = Filled();
        Assert.Equal(2, buffer.GetLatestIndexAt(2));
        Assert.Equal(4, buffer.GetLatestIndexAt(4));
        Assert.Equal(8, buffer.GetLatestIndexAt(8));
    }

    [Fact]
    public void ShouldReturnLatestOnUnknown()
    {
        var buffer = Filled();
        Assert.Equal(2, buffer.GetLatestIndexAt(3));
        Assert.Equal(4, buffer.GetLatestIndexAt(5));
        Assert.Equal(8, buffer.GetLatestIndexAt(9));
    }

    [Fact]
    public void SetAt_ShouldSetBehindTail()
    {
        var buffer = Filled();
        buffer.SetAt(1, "4");
        Assert.True(buffer.HasAt(1));
    }

    [Fact]
    public void SetAt_ShouldNotSetBehindLimit()
    {
        var buffer = Filled();
        buffer.SetAt(-8, "4");
        Assert.False(buffer.HasAt(-8));
    }

    [Fact]
    public void SetAt_ShouldUpdatePrevBufferIfInBounds()
    {
        var buffer = Filled();
        buffer.SetAt(6, "quoo");
        Assert.False(buffer.HasAt(7));
        Assert.True(buffer.HasLatestAt(7));
        Assert.Equal("quoo", buffer.GetLatestAt(7));
    }

    [Fact]
    public void SetAt_ShouldUpdatePrevBufferIfAfterHead()
    {
        var buffer = Filled();
        buffer.SetAt(14, "quoo");
        Assert.False(buffer.HasAt(11));
        Assert.True(buffer.HasLatestAt(11));
        Assert.Equal("baz", buffer.GetLatestAt(11));
    }

    [Fact]
    public void SetAt_ShouldJumpIfWayAfterHead()
    {
        var buffer = Filled();
        buffer.SetAt(130, "quoo");
        Assert.False(buffer.HasAt(8));
        Assert.False(buffer.HasLatestAt(8));
        Assert.Equal(1, buffer.Size);
    }

    [Fact]
    public void Values_ShouldReturnEmpty() => Assert.Empty(new HistoryBuffer<string>().Values());

    [Fact]
    public void Values_ShouldReturnEmptyAfterClear()
    {
        var buffer = Filled();
        buffer.Clear();
        Assert.Empty(buffer.Values());
    }

    [Fact]
    public void Values_ShouldSkipGaps()
    {
        var buffer = HistoryBuffer<string>.Of(16, new Dictionary<int, string> { [2] = "hello", [4] = "world", [15] = "!" });
        Assert.Equal(["hello", "world", "!"], buffer.Values());
    }

    [Fact]
    public void Push_ShouldSlideTailWhenFull()
    {
        var buffer = new HistoryBuffer<int>(4);
        for (var i = 0; i < 6; i++) buffer.Push(i);
        Assert.Equal(4, buffer.Size);
        Assert.Equal(2, buffer.EarliestIndex);
        Assert.Equal(5, buffer.LatestIndex);
        Assert.False(buffer.HasAt(1));
        Assert.Equal(5, buffer.GetAt(5));
    }
}
