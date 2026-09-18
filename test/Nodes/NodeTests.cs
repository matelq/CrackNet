using CrackNet.Core.Serialization;
using Godot;


namespace CrackNet.Tests;

public partial class GodotInteropAssumptionTests : TestSuite
{
    [Test]
    public void NodePath_ShouldBeValueEqualAsDictionaryKey()
    {
        var dictionary = new Dictionary<NodePath, int> { [new NodePath("position")] = 1, [new NodePath("Head:transform")] = 2 };
        Expect.True(dictionary.ContainsKey(new NodePath("position")));
        Expect.Equal(2, dictionary[new NodePath("Head:transform")]);
        Expect.False(dictionary.ContainsKey(new NodePath("rotation")));
    }

    [Test]
    public void GetIndexed_ShouldReadAndWriteSubProperties()
    {
        var node = new Node3D { Position = new Vector3(1, 2, 3) };
        Expect.Approx(2.0, node.GetIndexed("position:y").AsDouble());
        node.SetIndexed("position:x", 9.0f);
        Expect.Approx(9.0, node.Position.X);
        node.Free();
    }
}

public partial class TickrateHandshakeTests : TestSuite
{
    private partial class TestingHandshake : NetworkTickrateHandshake
    {
        public bool Authority;
        protected override bool IsAuthority() => Authority;
    }

    private int _baseTickrate;

    public override Task BeforeCase()
    {
        _baseTickrate = NetworkTime.Instance.Tickrate;
        return Task.CompletedTask;
    }

    public override Task AfterCase()
    {
        NetworkTime.Instance.Tickrate = _baseTickrate;
        return Task.CompletedTask;
    }

    [Test]
    public async Task ClientShouldAdjustOnMismatch()
    {
        var handshake = await Mount(new TestingHandshake { Authority = false, MismatchAction = TickrateMismatchAction.Adjust });
        handshake.ReceiveTickrate(0, 48);
        Expect.Equal(48, NetworkTime.Instance.Tickrate);
    }

    [Test]
    public async Task ServerShouldNotAdjustOnMismatch()
    {
        var handshake = await Mount(new TestingHandshake { Authority = true, MismatchAction = TickrateMismatchAction.Adjust });
        handshake.ReceiveTickrate(0, 48);
        Expect.Equal(_baseTickrate, NetworkTime.Instance.Tickrate);
    }

    [Test]
    public async Task ClientShouldEmitSignalOnMismatch()
    {
        var handshake = await Mount(new TestingHandshake { Authority = false, MismatchAction = TickrateMismatchAction.Signal });
        var emissions = new List<(int, int)>();
        handshake.OnTickrateMismatch += (peer, tickrate) => emissions.Add((peer, tickrate));
        handshake.ReceiveTickrate(0, 48);
        Expect.SequenceEqual([(0, 48)], emissions);
    }
}
