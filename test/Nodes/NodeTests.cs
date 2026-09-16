using Godot;
using Netfox.Core.Serialization;


namespace Netfox.Tests;

public partial class PeerVisibilityFilterTests : TestSuite
{
    private static readonly int[] Peers = [1, 2, 3, 4];

    [Test]
    public void ShouldReturnAllPeersOnDefaultVisibility()
    {
        var filter = new PeerVisibilityFilter { DefaultVisibility = true };
        filter.UpdateVisibility(Peers);
        Expect.SequenceEqual(Peers, filter.GetVisiblePeers());
        Expect.SequenceEqual([0], filter.GetRpcTargetPeers());
        filter.Free();
    }

    [Test]
    public void ShouldReturnNoPeersOnDefaultInvisibility()
    {
        var filter = new PeerVisibilityFilter { DefaultVisibility = false };
        filter.UpdateVisibility(Peers);
        Expect.Empty(filter.GetVisiblePeers());
        Expect.Empty(filter.GetRpcTargetPeers());
        filter.Free();
    }

    [Test]
    public void ShouldReturnForceVisiblePeers()
    {
        var filter = new PeerVisibilityFilter { DefaultVisibility = false };
        filter.SetVisibilityFor(2, true);
        filter.UpdateVisibility(Peers);
        Expect.True(filter.GetVisibilityFor(2));
        Expect.False(filter.GetVisibilityFor(1));
        Expect.SequenceEqual([2], filter.GetVisiblePeers());
        Expect.SequenceEqual([2], filter.GetRpcTargetPeers());
        filter.Free();
    }

    [Test]
    public void ShouldExcludeSingleInvisiblePeer()
    {
        var filter = new PeerVisibilityFilter { DefaultVisibility = true };
        filter.SetVisibilityFor(2, false);
        filter.UpdateVisibility(Peers);
        Expect.SequenceEqual([1, 3, 4], filter.GetVisiblePeers());
        Expect.SequenceEqual([-2], filter.GetRpcTargetPeers());
        filter.Free();
    }

    [Test]
    public void ShouldReturnPeersWithMultipleExcludes()
    {
        var filter = new PeerVisibilityFilter { DefaultVisibility = true };
        filter.SetVisibilityFor(2, false);
        filter.SetVisibilityFor(4, false);
        filter.UpdateVisibility(Peers);
        Expect.SequenceEqual([1, 3], filter.GetVisiblePeers());
        Expect.SequenceEqual([1, 3], filter.GetRpcTargetPeers());
        filter.Free();
    }

    [Test]
    public void Filters_ShouldExcludeIfAnyReturnsFalse()
    {
        var filter = new PeerVisibilityFilter { DefaultVisibility = true };
        filter.AddVisibilityFilter(_ => true);
        filter.UpdateVisibility(Peers);
        Expect.SequenceEqual(Peers, filter.GetVisiblePeers());

        filter.AddVisibilityFilter(_ => false);
        filter.UpdateVisibility(Peers);
        Expect.Empty(filter.GetVisiblePeers());
        filter.Free();
    }

    /// <summary>
    /// Filters only ever subtract: a filter returning true does not grant visibility, it merely declines to take it
    /// away, and the default is what is left. Pairing a filter with DefaultVisibility = false therefore hides the
    /// node from everyone - which reads as a replication bug rather than as filtering, and did exactly that in the
    /// playground sample's beacon.
    /// </summary>
    [Test]
    public void FilterReturningTrueShouldNotOverrideDefaultInvisibility()
    {
        var filter = new PeerVisibilityFilter { DefaultVisibility = false };
        filter.AddVisibilityFilter(_ => true);
        filter.UpdateVisibility(Peers);
        Expect.Empty(filter.GetVisiblePeers());

        // What does work with a default of false is a per-peer override, which is consulted after the filters
        filter.SetVisibilityFor(3, true);
        filter.UpdateVisibility(Peers);
        Expect.SequenceEqual([3], filter.GetVisiblePeers());
        filter.Free();
    }

    [Test]
    public void FilterShouldHavePrecedenceOverOverride()
    {
        var filter = new PeerVisibilityFilter { DefaultVisibility = false };
        filter.SetVisibilityFor(2, true);
        filter.AddVisibilityFilter(peer => peer != 2);
        filter.UpdateVisibility(Peers);
        Expect.Empty(filter.GetVisiblePeers());
        filter.Free();
    }
}


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
