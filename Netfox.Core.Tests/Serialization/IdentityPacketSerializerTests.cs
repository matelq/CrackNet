using Netfox.Core.Serialization;

namespace Netfox.Core.Tests.Serialization;

public class IdentityPacketSerializerTests
{
    [Fact]
    public void ShouldDeserializeToSame()
    {
        var ids = new Dictionary<string, int>
        {
            ["Some Node"] = 1,
            ["Another Node"] = 2,
            ["Some Node/Input"] = 3,
        };
        var serialized = IdentityPacketSerializer.Serialize(ids);
        Assert.Equal(ids, IdentityPacketSerializer.Deserialize(serialized));
    }
}
