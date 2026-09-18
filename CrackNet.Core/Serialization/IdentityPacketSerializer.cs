namespace CrackNet.Core.Serialization;

/// <summary>Serializes (full name, local id) pairs, used when sending local ids to other peers. Port of serializers/identity-packet-serializer.gd.</summary>
public static class IdentityPacketSerializer
{
    public static byte[] Serialize(IReadOnlyDictionary<string, int> ids)
    {
        var buffer = new ByteWriter();
        foreach (var (fullName, id) in ids)
        {
            buffer.PutUtf8String(fullName);
            VarUint.Encode(id, buffer);
        }
        return buffer.ToArray();
    }

    public static Dictionary<string, int> Deserialize(ReadOnlyMemory<byte> data)
    {
        var ids = new Dictionary<string, int>();
        var buffer = new ByteReader(data);
        while (buffer.AvailableBytes > 0)
        {
            var fullName = buffer.GetUtf8String();
            ids[fullName] = VarUint.DecodeInt(buffer);
        }
        return ids;
    }
}
