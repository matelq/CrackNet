using Netfox.Core.Serialization;

namespace Netfox.Core.Tests.Serialization;

public class TicksetSerializerTests
{
    [Fact]
    public void ShouldSerializeEmpty()
    {
        var bytes = TicksetSerializer.Serialize(15, 35, []);
        var (earliest, latest, active) = TicksetSerializer.Deserialize(bytes);
        Assert.Equal(15, earliest);
        Assert.Equal(35, latest);
        Assert.Empty(active);
    }

    [Fact]
    public void ShouldSerializeTicks()
    {
        var bytes = TicksetSerializer.Serialize(15, 35, [15, 16, 20, 24]);
        var (earliest, latest, active) = TicksetSerializer.Deserialize(bytes);
        Assert.Equal(15, earliest);
        Assert.Equal(35, latest);
        Assert.Equal(new HashSet<int> { 15, 16, 20, 24 }, active);
    }

    [Fact]
    public void ShouldSerializeOnlyRange()
    {
        var bytes = TicksetSerializer.Serialize(16, 20, [24, 15, 20, 16]);
        var (_, _, active) = TicksetSerializer.Deserialize(bytes);
        Assert.Equal(new HashSet<int> { 16, 20 }, active);
    }

    [Fact]
    public void ShouldFailOnEarliestAfterLatest()
        => Assert.Empty(TicksetSerializer.Serialize(42, 12, []));

    [Fact]
    public void ShouldUseFiveByteHeaderPlusOneBytePerTick()
    {
        Assert.Equal(5, TicksetSerializer.Serialize(15, 35, []).Length);
        Assert.Equal(9, TicksetSerializer.Serialize(15, 35, [15, 16, 20, 24]).Length);
    }
}
