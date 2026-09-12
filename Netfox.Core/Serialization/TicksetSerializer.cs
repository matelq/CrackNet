using Netfox.Core.Logging;

namespace Netfox.Core.Serialization;

/// <summary>Compact encoding of a set of active ticks within a range of at most 255 ticks. Port of serializers/tickset-serializer.gd.</summary>
public static class TicksetSerializer
{
    private static readonly NetfoxLogger Logger = NetfoxLogger.ForNetfox("TicksetSerializer");

    /// <returns>Encoded bytes, or an empty array if the range is invalid.</returns>
    public static byte[] Serialize(int earliestTick, int latestTick, IEnumerable<int> activeTicks)
    {
        var duration = latestTick - earliestTick;
        if (latestTick < earliestTick)
        {
            Logger.Error("Tickset ends before it starts! {0} > {1}", earliestTick, latestTick);
            return Array.Empty<byte>();
        }
        if (duration > 255)
        {
            Logger.Error("Tickset covers more than supported 255 ticks! {0}", duration);
            return Array.Empty<byte>();
        }

        var buffer = new ByteWriter();
        buffer.PutU32((uint)earliestTick);
        buffer.PutU8((byte)duration);

        foreach (var tick in activeTicks.Order())
        {
            if (tick < earliestTick) continue;
            if (tick > latestTick) break;
            buffer.PutU8((byte)(tick - earliestTick));
        }

        return buffer.ToArray();
    }

    public static (int EarliestTick, int LatestTick, HashSet<int> ActiveTicks) Deserialize(ReadOnlyMemory<byte> bytes)
    {
        var buffer = new ByteReader(bytes);
        var earliest = (int)buffer.GetU32();
        var duration = buffer.GetU8();
        var latest = earliest + duration;

        var active = new HashSet<int>();
        while (buffer.AvailableBytes > 0)
            active.Add(earliest + buffer.GetU8());

        return (earliest, latest, active);
    }
}
