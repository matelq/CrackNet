using Godot;
using Netfox.Core.Collections;
using Netfox.Core.Logging;
using Netfox.Core.Serialization;

namespace Netfox;

/// <summary>Shared identity and schema handling for snapshot serializers. Port of serializers/base-snapshot-serializer.gd.</summary>
public abstract class BaseSnapshotSerializer
{
    private NetworkIdentityServer? _identityServer;

    protected readonly NetfoxLogger Logger;
    protected readonly NetworkSchema Schemas;

    public int MaxPacketSize { get; set; } = 1440;

    // Reused across calls: a serializer writes for one peer at a time, on the main loop, and never nests
    protected readonly ByteWriter FrameBuffer = new();
    protected readonly ByteWriter NodeBuffer = new();
    private readonly PacketBuffer _packets = new();
    private readonly Action<ByteWriter> _writeTickHeader;
    private int _packetTick;

    /// <summary>The shared packet buffer, set up to prefix each packet with <paramref name="tick"/>.</summary>
    protected PacketBuffer PacketsFor(int tick)
    {
        _packetTick = tick;
        _packets.MaxPacketSize = MaxPacketSize;
        _packets.PacketSetup = _writeTickHeader;
        return _packets;
    }

    protected BaseSnapshotSerializer(string name, NetworkSchema schemas, NetworkIdentityServer? identityServer)
    {
        _writeTickHeader = packet => packet.PutU32((uint)_packetTick);
        Logger = NetfoxLogger.ForNetfox(name);
        // Intentionally storing the reference so it can be modified from the outside
        Schemas = schemas;
        _identityServer = identityServer;
    }

    protected NetworkIdentityServer IdentityServer => _identityServer ??= NetworkIdentityServer.Instance;

    protected void WriteProperty(Node node, NodePath property, Variant value, ByteWriter buffer)
        => Schemas.GetSerializer(node, property).Encode(value, buffer);

    /// <summary>Returns Nil if the buffer ends before the property could be read.</summary>
    protected Variant ReadProperty(Node node, NodePath property, ByteReader buffer)
    {
        try
        {
            return Schemas.GetSerializer(node, property).Decode(buffer);
        }
        catch (EndOfStreamException)
        {
            buffer.Position = buffer.Length;
            return default;
        }
    }

    protected bool WriteIdentifier(Node subject, int peer, ByteWriter buffer)
    {
        var identifier = IdentityServer.GetIdentifierOf(subject);
        if (identifier is null)
        {
            Logger.Error("Cannot synchronize {0}, identifier missing!", subject);
            return false;
        }
        NetRef.Encode(identifier.ReferenceFor(peer), buffer);
        return true;
    }

    protected Node? ResolveSubject(int peer, ByteReader buffer, out bool stop)
    {
        var reference = NetRef.Decode(buffer);
        var identifier = IdentityServer.ResolveReference(peer, reference);
        stop = false;
        if (identifier is null)
        {
            // A name we do not know is a node that has not spawned here yet, and it resolves itself: until the peer
            // has acked an id for a subject, the sender sends that subject in full rather than as a diff, so the
            // first frame we can read is a complete one. An id we do not know is a node deregistered since.
            Logger.Debug("Received unknown identity reference {0} from #{1}, skipping its frame", reference, peer);
            return null;
        }
        return identifier.Subject;
    }
}

/// <summary>Full state: every registered property of every auth subject. Port of serializers/dense-snapshot-serializer.gd.</summary>
public sealed class DenseSnapshotSerializer : BaseSnapshotSerializer
{
    public DenseSnapshotSerializer(NetworkSchema schemas, NetworkIdentityServer? identityServer = null)
        : base("DenseSnapshotSerializer", schemas, identityServer) { }

    public List<byte[]> WriteFor(int peer, Snapshot snapshot, PropertyPool properties, Func<Node, bool>? filter = null)
    {
        var packetBuffer = PacketsFor(snapshot.Tick);
        var frameBuffer = FrameBuffer;
        var nodeBuffer = NodeBuffer;

        foreach (var node in properties.Subjects)
        {
            if (filter is not null && !filter(node)) continue;
            if (!snapshot.IsAuth(node)) continue;

            frameBuffer.Clear();
            nodeBuffer.Clear();

            if (!WriteIdentifier(node, peer, frameBuffer)) continue;

            foreach (var property in properties.GetPropertiesOf(node))
            {
                if (!snapshot.TryGetProperty(node, property, out var value))
                {
                    Logger.Error("Trying to serialize missing property {0} on subject {1}!", property, node);
                    value = default;
                }
                WriteProperty(node, property, value, nodeBuffer);
            }

            VarUint.Encode(nodeBuffer.Size, frameBuffer);
            frameBuffer.PutData(nodeBuffer.WrittenSpan);
            packetBuffer.Push(frameBuffer.WrittenSpan);
        }

        return packetBuffer.Finish();
    }

    public Snapshot ReadFrom(int peer, PropertyPool properties, ByteReader buffer, bool isAuth = true)
    {
        var tick = (int)buffer.GetU32();
        var snapshot = new Snapshot(tick);

        while (buffer.AvailableBytes > 0)
        {
            var node = ResolveSubject(peer, buffer, out _);
            var nodeDataSize = VarUint.DecodeInt(buffer);
            var nodeBuffer = new ByteReader(buffer.GetPartialData(nodeDataSize));
            if (node is null) continue;

            foreach (var property in properties.GetPropertiesOf(node))
            {
                if (nodeBuffer.AvailableBytes == 0) break;
                snapshot.SetProperty(node, property, ReadProperty(node, property, nodeBuffer));
            }
            snapshot.SetAuth(node, isAuth);
        }

        return snapshot;
    }
}

/// <summary>Diff state: only properties present in the snapshot, flagged by a bitset. Port of serializers/sparse-snapshot-serializer.gd.</summary>
public sealed class SparseSnapshotSerializer : BaseSnapshotSerializer
{
    public SparseSnapshotSerializer(NetworkSchema schemas, NetworkIdentityServer? identityServer = null)
        : base("SparseSnapshotSerializer", schemas, identityServer) { }

    public List<byte[]> WriteFor(int peer, Snapshot snapshot, PropertyPool properties, Func<Node, bool>? filter = null)
    {
        var packetBuffer = PacketsFor(snapshot.Tick);
        var frameBuffer = FrameBuffer;
        var nodeBuffer = NodeBuffer;

        foreach (var node in properties.Subjects)
        {
            if (filter is not null && !filter(node)) continue;
            if (!snapshot.IsAuth(node)) continue;

            frameBuffer.Clear();
            nodeBuffer.Clear();

            if (!WriteIdentifier(node, peer, frameBuffer)) continue;

            var nodeProps = properties.GetPropertiesOf(node);
            var changedBits = new Bitset(nodeProps.Count);

            for (var i = 0; i < nodeProps.Count; i++)
            {
                if (!snapshot.TryGetProperty(node, nodeProps[i], out var value)) continue;
                changedBits.SetBit(i);
                WriteProperty(node, nodeProps[i], value, nodeBuffer);
            }

            VarUint.Encode(nodeBuffer.Size, frameBuffer);
            VarBits.Encode(changedBits, frameBuffer);
            frameBuffer.PutData(nodeBuffer.WrittenSpan);
            packetBuffer.Push(frameBuffer.WrittenSpan);
        }

        return packetBuffer.Finish();
    }

    public Snapshot ReadFrom(int peer, PropertyPool properties, ByteReader buffer, bool isAuth = true)
    {
        var tick = (int)buffer.GetU32();
        var snapshot = new Snapshot(tick);

        while (buffer.AvailableBytes > 0)
        {
            var node = ResolveSubject(peer, buffer, out _);
            var nodeDataSize = VarUint.DecodeInt(buffer);
            var changedBits = VarBits.Decode(buffer);
            var nodeBuffer = new ByteReader(buffer.GetPartialData(nodeDataSize));
            // The frame is already consumed, so the ones after it are still readable. Upstream gives up on the whole
            // packet here (sparse-snapshot-serializer.gd:24), which loses every node behind the unknown one.
            if (node is null) continue;

            var nodeProps = properties.GetPropertiesOf(node);
            foreach (var idx in changedBits.GetSetIndices())
            {
                if (idx >= nodeProps.Count) break;
                snapshot.SetProperty(node, nodeProps[idx], ReadProperty(node, nodeProps[idx], nodeBuffer));
            }
            snapshot.SetAuth(node, isAuth);
        }

        return snapshot;
    }
}

/// <summary>Packs several dense snapshots into one packet, for input redundancy. Port of serializers/redundant-snapshot-serializer.gd.</summary>
public sealed class RedundantSnapshotSerializer : BaseSnapshotSerializer
{
    private readonly DenseSnapshotSerializer _dense;

    public RedundantSnapshotSerializer(NetworkSchema schemas, NetworkIdentityServer? identityServer = null)
        : base("RedundantSnapshotSerializer", schemas, identityServer)
    {
        _dense = new DenseSnapshotSerializer(schemas, identityServer);
    }

    public byte[] WriteFor(int peer, IReadOnlyList<Snapshot> snapshots, PropertyPool properties)
    {
        _dense.MaxPacketSize = MaxPacketSize;
        var buffer = new ByteWriter();

        // TODO(#560): Encode the first snapshot as-is and the rest as diffs
        foreach (var snapshot in snapshots)
        {
            var densePackets = _dense.WriteFor(peer, snapshot, properties);
            if (densePackets.Count == 0) continue;
            if (densePackets.Count > 1)
                Logger.Warning("Redundant snapshot does not fit into a single packet! Max packet size: {0} bytes", MaxPacketSize);

            var densePacket = densePackets[0];
            VarUint.Encode(densePacket.Length, buffer);
            buffer.PutData(densePacket);
        }

        if (buffer.Size > MaxPacketSize)
            Logger.Warning("Redundant data does not fit into a single packet! Max packet size: {0} bytes", MaxPacketSize);

        return buffer.ToArray();
    }

    public List<Snapshot> ReadFrom(int peer, PropertyPool properties, ByteReader buffer, bool isAuth = true)
    {
        var snapshots = new List<Snapshot>();
        while (buffer.AvailableBytes > 0)
        {
            var snapshotSize = VarUint.DecodeInt(buffer);
            var snapshotBuffer = new ByteReader(buffer.GetPartialData(snapshotSize));
            snapshots.Add(_dense.ReadFrom(peer, properties, snapshotBuffer, isAuth));
        }
        return snapshots;
    }
}
