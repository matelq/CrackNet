using Netfox.Core.Logging;

namespace Netfox.Tests;

/// <summary>State packets stay under the size limit, and an object that cannot fit says so instead of vanishing.</summary>
public partial class PacketSizeTests : HarnessSuite
{
    [Test]
    public async Task AnObjectLargerThanAPacketWarnsOnce()
    {
        var printed = new List<string>();
        var print = NetfoxLogger.Print;
        NetfoxLogger.Print = line =>
        {
            printed.Add(line);
            print(line);
        };
        try
        {
            var blob = new string('x', NetfoxSettings.Instance.MaxSyncPacketSize * 2);
            HarnessBlob.Spawn(Host, "Blob", 1, blob);
            var onClient = HarnessBlob.Spawn(Client, "Blob", 1, "");
            var received = 0;
            onClient.Object.SampleReceived += _ => received++;

            Expect.True(await WaitUntil(() => received >= 3, 5), "the oversized object never arrived");
            var warnings = printed.Count(line => line.Contains("Blob") && line.Contains("larger than"));
            Expect.Equal(1, warnings, string.Join("\n", printed.Where(line => line.Contains("Blob"))));
        }
        finally
        {
            NetfoxLogger.Print = print;
        }
    }
}
