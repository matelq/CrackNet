using Godot;
using Netfox.Core.Serialization;

namespace Netfox.Tests;

public partial class SnapshotSerializerTests : TestSuite
{
    private static readonly NodePath Position = "position";
    private static readonly NodePath Quaternion = "quaternion";
    private static readonly NodePath Scale = "scale";

    private async Task<Node3D> Subject()
    {
        var node = await Mount(new Node3D());
        NetworkIdentityServer.Instance.RegisterNode(node);
        return node;
    }

    public override Task AfterCase()
    {
        foreach (var child in GetChildren())
            if (child is Node3D n) NetworkIdentityServer.Instance.DeregisterNode(n);
        return Task.CompletedTask;
    }

    private static Snapshot FullSnapshot(Node subject) => Snapshot.Of(0, [
        (subject, Position, Vector3.Zero),
        (subject, Quaternion, Godot.Quaternion.FromEuler(Vector3.One)),
        (subject, Scale, Vector3.One),
    ], [subject]);

    private static PropertyPool Props(Node subject) => PropertyPool.Of([(subject, Position), (subject, Quaternion), (subject, Scale)]);

    [Test]
    public async Task Dense_ShouldDeserializeToSame()
    {
        var serializer = new DenseSnapshotSerializer(new NetworkSchema(NetworkSchemas.Variant()));
        var subject = await Subject();
        var snapshot = FullSnapshot(subject);
        var props = Props(subject);

        var packets = serializer.WriteFor(1, snapshot, props);
        Expect.Equal(1, packets.Count);
        var deserialized = serializer.ReadFrom(1, props, new ByteReader(packets[0]));
        Expect.Equal(snapshot, deserialized);
        GD.Print($"      Dense: {snapshot.Size} props to {packets[0].Length} bytes");
    }

    [Test]
    public async Task Dense_ShouldIgnoreUnknownIdentifiers()
    {
        var schema = new NetworkSchema(NetworkSchemas.Variant());
        var writerIdentity = await Mount(new NetworkIdentityServer(await Mount(new TestingCommandServer())));
        var readerIdentity = await Mount(new NetworkIdentityServer(await Mount(new TestingCommandServer())));
        var writer = new DenseSnapshotSerializer(schema, writerIdentity);
        var reader = new DenseSnapshotSerializer(schema, readerIdentity);

        var known = await Mount(new Node3D());
        var unknown = await Mount(new Node3D());
        var writerProps = PropertyPool.Of([(known, Position), (unknown, Position)]);
        var readerProps = PropertyPool.Of([(known, Position)]);

        writerIdentity.RegisterNode(known);
        writerIdentity.RegisterNode(unknown);
        readerIdentity.RegisterNode(known);

        var snapshot = Snapshot.Of(0, [(unknown, Position, Vector3.Zero), (known, Position, Vector3.Zero)], [known, unknown]);
        var expected = Snapshot.Of(0, [(known, Position, Vector3.Zero)], [known]);

        var packets = writer.WriteFor(1, snapshot, writerProps);
        Expect.Equal(expected, reader.ReadFrom(1, readerProps, new ByteReader(packets[0])));
    }

    [Test]
    public async Task Dense_ShouldHandleMissingData()
    {
        var serializer = new DenseSnapshotSerializer(new NetworkSchema(NetworkSchemas.Variant()));
        var subject = await Subject();
        var snapshot = FullSnapshot(subject);
        var props = Props(subject);

        var expected = Snapshot.Of(0, [
            (subject, Position, Vector3.Zero),
            (subject, Quaternion, Godot.Quaternion.FromEuler(Vector3.One)),
            (subject, Scale, default(Variant)),
        ], [subject]);

        var packet = serializer.WriteFor(1, snapshot, props)[0];
        var truncated = packet[..^4];
        Expect.Equal(expected, serializer.ReadFrom(1, props, new ByteReader(truncated)));
    }

    [Test]
    public async Task Sparse_ShouldDeserializeToSame()
    {
        var serializer = new SparseSnapshotSerializer(new NetworkSchema(NetworkSchemas.Variant()));
        var subject = await Subject();
        var snapshot = FullSnapshot(subject);
        var props = Props(subject);

        var packets = serializer.WriteFor(1, snapshot, props);
        Expect.Equal(1, packets.Count);
        Expect.Equal(snapshot, serializer.ReadFrom(1, props, new ByteReader(packets[0])));
    }

    [Test]
    public async Task Sparse_ShouldOnlyCarryPresentProperties()
    {
        var serializer = new SparseSnapshotSerializer(new NetworkSchema(NetworkSchemas.Variant()));
        var subject = await Subject();
        var diff = Snapshot.Of(3, [(subject, Scale, Vector3.One * 2)], [subject]);
        var props = Props(subject);

        var packets = serializer.WriteFor(1, diff, props);
        var read = serializer.ReadFrom(1, props, new ByteReader(packets[0]));
        Expect.Equal(diff, read);
        Expect.False(read.HasProperty(subject, Position));
    }

    /// <summary>
    /// #15: a reference the reader cannot resolve costs that one node's frame, not the rest of the packet. Upstream
    /// stops parsing here, so every node written behind the unknown one is lost too.
    /// </summary>
    [Test]
    public async Task Sparse_ShouldSkipUnknownIdentifiersAndKeepReading()
    {
        var schema = new NetworkSchema(NetworkSchemas.Variant());
        var writerIdentity = await Mount(new NetworkIdentityServer(await Mount(new TestingCommandServer())));
        var readerIdentity = await Mount(new NetworkIdentityServer(await Mount(new TestingCommandServer())));
        var writer = new SparseSnapshotSerializer(schema, writerIdentity);
        var reader = new SparseSnapshotSerializer(schema, readerIdentity);

        var unknown = await Mount(new Node3D());
        var known = await Mount(new Node3D());

        // Unknown first, so giving up on it would take the known node with it
        var writerProps = PropertyPool.Of([(unknown, Position), (known, Position)]);
        var readerProps = PropertyPool.Of([(known, Position)]);

        writerIdentity.RegisterNode(unknown);
        writerIdentity.RegisterNode(known);
        readerIdentity.RegisterNode(known);

        var snapshot = Snapshot.Of(4, [(unknown, Position, Vector3.Up), (known, Position, Vector3.Right)], [unknown, known]);
        var expected = Snapshot.Of(4, [(known, Position, Vector3.Right)], [known]);

        var packets = writer.WriteFor(1, snapshot, writerProps);
        Expect.Equal(expected, reader.ReadFrom(1, readerProps, new ByteReader(packets[0])));
    }

    /// <summary>
    /// #14: the older copies say only how they differ from the newest one. Held input does not change from tick to
    /// tick, which is the case the redundancy exists for, so those copies should cost almost nothing.
    /// </summary>
    [Test]
    public async Task Redundant_ShouldEncodeUnchangedCopiesAsAlmostNothing()
    {
        var serializer = new RedundantSnapshotSerializer(new NetworkSchema(NetworkSchemas.Variant()));
        var subject = await Subject();
        var props = Props(subject);

        // Once a peer has acked an id, frames refer to the subject by that id rather than by its whole path, which is
        // what makes the header of an unchanged copy small
        // Reading back in the same process means the id has to be the one this server resolves locally
        var identifier = NetworkIdentityServer.Instance.GetIdentifierOf(subject)!;
        identifier.SetIdFor(1, identifier.LocalId);

        Snapshot Held(int tick) => Snapshot.Of(tick, [
            (subject, Position, Vector3.Zero),
            (subject, Quaternion, Godot.Quaternion.FromEuler(Vector3.One)),
            (subject, Scale, Vector3.One),
        ], [subject]);

        var one = serializer.WriteFor(1, [Held(9)], props);
        var three = serializer.WriteFor(1, [Held(9), Held(8), Held(7)], props);

        // Two more ticks of the same input, for a header each rather than a copy each
        Expect.True(three.Length < one.Length * 1.5,
            $"three identical snapshots took {three.Length} bytes against {one.Length} for one");
        GD.Print($"      Redundant held input: 1 snapshot {one.Length} bytes, 3 snapshots {three.Length} bytes");

        // And they still come back whole, ticks and all
        var read = serializer.ReadFrom(1, props, new ByteReader(three));
        Expect.Equal(3, read.Count);
        Expect.SequenceEqual([9, 8, 7], read.Select(snapshot => snapshot.Tick));
        Expect.Equal(Held(8), read[1]);
        Expect.Equal(Held(7), read[2]);
    }

    /// <summary>A subject that only appears in the newest tick must not be attributed to the older ones.</summary>
    [Test]
    public async Task Redundant_ShouldNotBackdateASubjectThatOnlyTheNewestTickHas()
    {
        var serializer = new RedundantSnapshotSerializer(new NetworkSchema(NetworkSchemas.Variant()));
        var standing = await Subject();
        var spawned = await Subject();
        var props = PropertyPool.Of([(standing, Position), (spawned, Position)]);

        var newest = Snapshot.Of(9, [(standing, Position, Vector3.One), (spawned, Position, Vector3.Up)], [standing, spawned]);
        var older = Snapshot.Of(8, [(standing, Position, Vector3.One)], [standing]);

        var read = serializer.ReadFrom(1, props, new ByteReader(serializer.WriteFor(1, [newest, older], props)));

        Expect.Equal(2, read.Count);
        Expect.Equal(newest, read[0]);
        Expect.Equal(older, read[1]);
        Expect.False(read[1].HasProperty(spawned, Position), "the older tick never had this subject");
    }

    [Test]
    public async Task Redundant_ShouldDeserializeToSame()
    {
        var serializer = new RedundantSnapshotSerializer(new NetworkSchema(NetworkSchemas.Variant()));
        var subject = await Subject();
        var props = Props(subject);

        var snapshots = new List<Snapshot>
        {
            Snapshot.Of(0, [(subject, Position, Vector3.Zero), (subject, Quaternion, Godot.Quaternion.FromEuler(Vector3.One)), (subject, Scale, Vector3.One)], [subject]),
            Snapshot.Of(1, [(subject, Position, new Vector3(1, 0, 0)), (subject, Quaternion, Godot.Quaternion.FromEuler(Vector3.Zero)), (subject, Scale, new Vector3(1, 0.5f, 1))], [subject]),
        };

        var serialized = serializer.WriteFor(1, snapshots, props);
        var deserialized = serializer.ReadFrom(1, props, new ByteReader(serialized));

        Expect.Equal(snapshots.Count, deserialized.Count);
        for (var i = 0; i < snapshots.Count; i++)
            Expect.Equal(snapshots[i], deserialized[i]);
        GD.Print($"      Redundant: {snapshots.Count} snapshots to {serialized.Length} bytes");
    }

    [Test]
    public async Task Dense_ShouldUseRegisteredSchema()
    {
        var schema = new NetworkSchema(NetworkSchemas.Variant());
        var subject = await Subject();
        schema.Add(subject, Position, NetworkSchemas.Vec3F32());
        var serializer = new DenseSnapshotSerializer(schema);

        var snapshot = Snapshot.Of(0, [(subject, Position, new Vector3(1, 2, 3))], [subject]);
        var props = PropertyPool.Of([(subject, Position)]);
        var packet = serializer.WriteFor(1, snapshot, props)[0];
        var variantPacket = new DenseSnapshotSerializer(new NetworkSchema(NetworkSchemas.Variant())).WriteFor(1, snapshot, props)[0];

        // Variant encoding costs a 4 byte length and a 4 byte type header on top of the raw 12 bytes
        Expect.Equal(variantPacket.Length - 8, packet.Length);
        Expect.Equal(snapshot, serializer.ReadFrom(1, props, new ByteReader(packet)));
    }
}

public partial class NetworkSchemasTests : TestSuite
{
    private static readonly (string Name, NetworkSchemaSerializer Serializer, Variant Value, int Size)[] Cases =
    [
        ("variant", NetworkSchemas.Variant(), 32, 12),
        ("string", NetworkSchemas.String(), "hi!!", 8),
        ("c_string", NetworkSchemas.CString(), "hi!!", 5),
        ("bool8", NetworkSchemas.Bool8(), true, 1),

        ("int8", NetworkSchemas.Int8(), 107, 1),
        ("int16", NetworkSchemas.Int16(), 107, 2),
        ("int32", NetworkSchemas.Int32(), 107, 4),
        ("int64", NetworkSchemas.Int64(), 107, 8),
        ("uint8", NetworkSchemas.Uint8(), 107, 1),
        ("uint16", NetworkSchemas.Uint16(), 107, 2),
        ("uint32", NetworkSchemas.Uint32(), 107, 4),
        ("uint64", NetworkSchemas.Uint64(), 107, 8),

        ("varuint 8", NetworkSchemas.Varuint(), (1L << 7) - 1, 1),
        ("varuint 16", NetworkSchemas.Varuint(), (1L << 14) - 1, 2),
        ("varuint 32", NetworkSchemas.Varuint(), (1L << 28) - 1, 4),
        ("varuint 64", NetworkSchemas.Varuint(), (1L << 56) - 1, 8),
        ("varuint 72", NetworkSchemas.Varuint(), long.MaxValue, 9),

        ("sfrac8", NetworkSchemas.Sfrac8(), -63.0 / 255.0, 1),
        ("sfrac16", NetworkSchemas.Sfrac16(), -63.0 / 255.0, 2),
        ("sfrac32", NetworkSchemas.Sfrac32(), -63.0 / 255.0, 4),
        ("ufrac8", NetworkSchemas.Ufrac8(), 63.0 / 255.0, 1),
        ("ufrac16", NetworkSchemas.Ufrac16(), 63.0 / 255.0, 2),
        ("ufrac32", NetworkSchemas.Ufrac32(), 63.0 / 255.0, 4),

        ("degrees8", NetworkSchemas.Degrees8(), 127.0 / 255.0 * 360.0, 1),
        ("degrees16", NetworkSchemas.Degrees16(), 127.0 / 255.0 * 360.0, 2),
        ("degrees32", NetworkSchemas.Degrees32(), 127.0 / 255.0 * 360.0, 4),
        ("radians8", NetworkSchemas.Radians8(), 127.0 / 255.0 * Math.Tau, 1),
        ("radians16", NetworkSchemas.Radians16(), 127.0 / 255.0 * Math.Tau, 2),
        ("radians32", NetworkSchemas.Radians32(), 127.0 / 255.0 * Math.Tau, 4),

        ("float16", NetworkSchemas.Float16(), 2.0, 2),
        ("float32", NetworkSchemas.Float32(), 2.0, 4),
        ("float64", NetworkSchemas.Float64(), 2.0, 8),

        ("vec2f16", NetworkSchemas.Vec2F16(), new Vector2(1, -1), 4),
        ("vec2f32", NetworkSchemas.Vec2F32(), new Vector2(1, -1), 8),
        ("vec2f64", NetworkSchemas.Vec2F64(), new Vector2(1, -1), 16),
        ("vec3f32", NetworkSchemas.Vec3F32(), new Vector3(1, -1, 0.5f), 12),
        ("vec3f64", NetworkSchemas.Vec3F64(), new Vector3(1, -1, 0.5f), 24),
        ("vec4f32", NetworkSchemas.Vec4F32(), new Vector4(1, -1, 0.5f, -5), 16),
        ("vec4f64", NetworkSchemas.Vec4F64(), new Vector4(1, -1, 0.5f, -5), 32),

        ("normal2f32", NetworkSchemas.Normal2F32(), Vector2.Right.Rotated(Mathf.Pi / 6), 4),
        ("normal2f64", NetworkSchemas.Normal2F64(), Vector2.Right.Rotated(Mathf.Pi / 6), 8),
        ("normal3f32", NetworkSchemas.Normal3F32(), Vector3.Up, 8),
        ("normal3f64", NetworkSchemas.Normal3F64(), Vector3.Up, 16),

        ("quatf32", NetworkSchemas.QuatF32(), Quaternion.FromEuler(Vector3.One), 16),
        ("quatf64", NetworkSchemas.QuatF64(), Quaternion.FromEuler(Vector3.One), 32),

        ("transform2f32", NetworkSchemas.Transform2F32(), Transform2D.Identity.Rotated(37), 24),
        ("transform2f64", NetworkSchemas.Transform2F64(), Transform2D.Identity.Rotated(37), 48),
        ("transform3f32", NetworkSchemas.Transform3F32(), Transform3D.Identity.Rotated(Vector3.One.Normalized(), 37), 48),
        ("transform3f64", NetworkSchemas.Transform3F64(), Transform3D.Identity.Rotated(Vector3.One.Normalized(), 37), 96),

        ("array", NetworkSchemas.ArrayOf(NetworkSchemas.Uint16()), new Godot.Collections.Array { 1, 2, 3 }, 8),
        ("dictionary", NetworkSchemas.Dictionary(NetworkSchemas.Uint16(), NetworkSchemas.Uint16()), new Godot.Collections.Dictionary { [1] = 32, [2] = 48 }, 10),
    ];

    [Test]
    public void AllSchemas_ShouldRoundtripWithExpectedSize()
    {
        var failures = new List<string>();
        foreach (var (name, serializer, value, size) in Cases)
        {
            var writer = new ByteWriter();
            serializer.Encode(value, writer);
            var decoded = serializer.Decode(new ByteReader(writer.ToArray()));

            if (!ApproximatelyEqual(value, decoded))
                failures.Add($"{name}: value mismatch, expected {value}, got {decoded}");
            if (writer.Size != size)
                failures.Add($"{name}: size mismatch, expected {size}, got {writer.Size}");
        }
        Expect.Empty(failures, string.Join("\n", failures));
    }

    [Test]
    public void Degrees_ShouldWrapNegative()
    {
        var writer = new ByteWriter();
        NetworkSchemas.Degrees16().Encode(-60.0, writer);
        var decoded = NetworkSchemas.Degrees16().Decode(new ByteReader(writer.ToArray())).AsDouble();
        Expect.Approx(300.0, decoded, 1.0);
    }

    [Test]
    public void Radians_ShouldWrapNegative()
    {
        var writer = new ByteWriter();
        NetworkSchemas.Radians16().Encode(-Math.Tau / 6.0, writer);
        var decoded = NetworkSchemas.Radians16().Decode(new ByteReader(writer.ToArray())).AsDouble();
        Expect.Approx(5.0 / 6.0 * Math.Tau, decoded, 1.0 / Math.Tau);
    }

    private static bool ApproximatelyEqual(Variant a, Variant b)
    {
        if (a.VariantType != b.VariantType) return false;
        return a.VariantType switch
        {
            Variant.Type.Float => Math.Abs(a.AsDouble() - b.AsDouble()) < 1e-3,
            Variant.Type.Vector2 => a.AsVector2().IsEqualApprox(b.AsVector2()),
            Variant.Type.Vector3 => a.AsVector3().IsEqualApprox(b.AsVector3()),
            Variant.Type.Vector4 => a.AsVector4().IsEqualApprox(b.AsVector4()),
            Variant.Type.Quaternion => a.AsQuaternion().IsEqualApprox(b.AsQuaternion()),
            Variant.Type.Transform2D => a.AsTransform2D().IsEqualApprox(b.AsTransform2D()),
            Variant.Type.Transform3D => a.AsTransform3D().IsEqualApprox(b.AsTransform3D()),
            _ => Internal.VariantComparer.Instance.Equals(a, b),
        };
    }
}
