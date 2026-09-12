using Netfox.Core.Logging;

namespace Netfox.Core.Serialization;

/// <summary>
/// Packs data chunks into packets of a specified size. Multiple chunks may go into a single packet,
/// as long as they fit MaxPacketSize. Port of serializers/packet-buffer.gd.
/// </summary>
public sealed class PacketBuffer
{
    private static readonly NetfoxLogger Logger = NetfoxLogger.ForNetfox("PacketBuffer");

    private readonly List<byte[]> _packets = new();
    private ByteWriter? _buffer;

    public int MaxPacketSize { get; set; }

    /// <summary>Called on every fresh packet before data is written, e.g. to add a header.</summary>
    public Action<ByteWriter>? PacketSetup { get; set; }

    public PacketBuffer(int maxPacketSize = 1440)
    {
        MaxPacketSize = maxPacketSize;
    }

    public void Push(ReadOnlySpan<byte> data)
    {
        if (NeedsNewPacket(data.Length))
        {
            if (_buffer is { Size: > 0 })
                _packets.Add(_buffer.ToArray());
            _buffer = new ByteWriter();
            PacketSetup?.Invoke(_buffer);
        }

        _buffer!.PutData(data);
    }

    public List<byte[]> Finish()
    {
        if (_buffer is { Size: > 0 })
            _packets.Add(_buffer.ToArray());
        _buffer = null;

        var result = new List<byte[]>(_packets);
        _packets.Clear();
        return result;
    }

    private bool NeedsNewPacket(int incoming)
    {
        if (_buffer is null) return true;
        if (MaxPacketSize <= 0) return false;
        if (incoming > MaxPacketSize)
        {
            Logger.Warning("Buffer of {0} bytes exceeds packet limit of {1}; packet may be dropped", incoming, MaxPacketSize);
            return true;
        }
        return _buffer.Size + incoming > MaxPacketSize;
    }
}
