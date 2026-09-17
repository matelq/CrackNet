using Godot;

namespace Netfox.Tests;

/// <summary>What a room of objects costs on the wire. Byte counts are stable between runs, unlike timings.</summary>
public partial class BandwidthTests : HarnessSuite
{
    private const int Moving = 50;
    private const int Resting = 150;

    /// <summary>
    /// Bytes per second the host sends one peer for 50 moving and 150 resting objects; the host sends this to every
    /// guest. Every object carries its full transform (about 49 bytes) by the API's contract, which took this room from
    /// about 17 to about 61 kB/s; quantization and deltas are what would bring it back down (see Deferred).
    /// </summary>
    private const double BudgetBytesPerSecond = 67_000;

    [Test]
    public async Task ARoomOfObjectsFitsTheBudget()
    {
        for (var i = 0; i < Moving + Resting; i++)
        {
            var velocity = i < Moving ? new Vector3(1 + i % 5, 0, 0) : Vector3.Zero;
            HarnessBody.Spawn(Host, $"Body{i}", 1, velocity, new Vector3(i, 0, 0)).CountsTicks = i < Moving;
            HarnessBody.Spawn(Client, $"Body{i}", 1, velocity, new Vector3(i, 0, 0)).CountsTicks = i < Moving;
        }

        Expect.True(await WaitUntil(() => Client.Context.NetworkTime.IsInitialSyncDone(), 5), "client never synced");
        // Let identities settle: the first packets carry names until the ids are exchanged
        for (var i = 0; i < 60; i++) await NextFrame();

        // Object state only: the harness syncs clocks many times faster than a real session does
        var commands = Host.Context.NetworkCommandServer;
        commands.ResetSentCounts();
        var startTick = Host.Context.NetworkTime.Tick;
        await WaitUntil(() => Host.Context.NetworkTime.Tick >= startTick + 90, 10);
        var seconds = (Host.Context.NetworkTime.Tick - startTick) / (double)Host.Context.NetworkTime.Tickrate;

        var (bytes, packets) = commands.SentCounts.GetValueOrDefault(CommandIds.ObjectState);
        var perSecond = bytes / seconds;
        GD.Print($"BANDWIDTH moving={Moving} resting={Resting} bytes/s={perSecond:F0} packets/s={packets / seconds:F1} kbit/s={perSecond * 8 / 1000:F0}");

        Expect.True(perSecond < BudgetBytesPerSecond, $"{perSecond:F0} bytes/s over a budget of {BudgetBytesPerSecond}");
    }
}
