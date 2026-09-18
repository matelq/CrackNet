using CrackNet.Core.Serialization;

namespace CrackNet.Tests;

/// <summary>A packet that does not decode is dropped with a warning; it does not throw out of the transport.</summary>
public partial class MalformedPacketTests : HarnessSuite
{
    private static byte[] HugeLength()
    {
        var writer = new ByteWriter();
        writer.PutI32(10);
        writer.PutU8(0);
        VarUint.Encode(uint.MaxValue, writer);
        return writer.ToArray();
    }

    [Test]
    public void TruncatedOrOversizedPacketsAreDropped()
    {
        byte[][] packets = [[], [1], [1, 2, 3], HugeLength()];
        foreach (var id in new[] { CommandIds.ObjectState, CommandIds.ObjectAuthority, CommandIds.ObjectEvent })
            foreach (var packet in packets)
            {
                try
                {
                    Host.Context.NetworkCommandServer.HandleCommand(2, id, packet);
                }
                catch (Exception e)
                {
                    Expect.True(false, $"command #{id} with {packet.Length} bytes threw {e.GetType().Name}: {e.Message}");
                }
            }
    }
}
