using Godot;
using Netfox.Core.Serialization;

namespace Netfox.Tests;

/// <summary>Test doubles matching test/netfox/servers/testing-servers.gd: a command server that records instead of sending.</summary>
public partial class TestingCommandServer : NetworkCommandServer
{
    public readonly List<(int Idx, byte[] Data, int TargetPeer, MultiplayerPeer.TransferModeEnum Mode, int Channel)> CommandsSent = new();

    public override void SendCommand(int idx, byte[] data, int targetPeer = 0, MultiplayerPeer.TransferModeEnum mode = MultiplayerPeer.TransferModeEnum.Reliable, int channel = 0)
        => CommandsSent.Add((idx, data, targetPeer, mode, channel));
}

public partial class NetworkIdentityServerTests : TestSuite
{
    private TestingCommandServer _commands = null!;
    private NetworkIdentityServer _identity = null!;
    private Node _node = null!;
    private Node _orphan = null!;

    public override async Task BeforeCase()
    {
        _commands = await Mount(new TestingCommandServer());
        _identity = await Mount(new NetworkIdentityServer(_commands));
        _node = await Mount(new Node { Name = "Identified Node" });
        _orphan = new Node();
    }

    public override Task AfterCase()
    {
        _orphan.Free();
        return Task.CompletedTask;
    }

    [Test]
    public void RegisterNode_ShouldRegister()
    {
        _identity.RegisterNode(_node);
        var identifier = _identity.GetIdentifierOf(_node);
        Expect.NotNull(identifier);

        // Identities are named relative to the multiplayer root, which is /root here
        Expect.Equal(GetTree().Root.GetPathTo(_node).ToString(), identifier!.FullName);
    }

    [Test]
    public void RegisterNode_ShouldFailOnNodeNotInTree()
    {
        _identity.RegisterNode(_orphan);
        Expect.Null(_identity.GetIdentifierOf(_orphan));
    }

    [Test]
    public void DeregisterNode_ShouldRemoveKnownAndIgnoreUnknown()
    {
        _identity.RegisterNode(_node);
        Expect.NotNull(_identity.GetIdentifierOf(_node));
        _identity.DeregisterNode(_node);
        Expect.Null(_identity.GetIdentifierOf(_node));
        _identity.DeregisterNode(_orphan);
        Expect.Null(_identity.GetIdentifierOf(_orphan));
    }

    [Test]
    public void FlushQueue_ShouldSendIds()
    {
        _identity.RegisterNode(_node);
        var identifier = _identity.GetIdentifierOf(_node)!;

        _identity.ResolveReference(2, identifier.ReferenceFor(2));
        _identity.ResolveReference(3, identifier.ReferenceFor(3));
        _identity.FlushQueue();

        Expect.Equal(2, _commands.CommandsSent.Count);
        for (var i = 0; i < _commands.CommandsSent.Count; i++)
        {
            var command = _commands.CommandsSent[i];
            Expect.Equal(CommandIds.Identities, command.Idx);
            Expect.Equal(2 + i, command.TargetPeer);
            Expect.Equal(MultiplayerPeer.TransferModeEnum.Reliable, command.Mode);
            Expect.Equal(identifier.LocalId, IdentityPacketSerializer.Deserialize(command.Data)[identifier.FullName]);
        }
    }

    [Test]
    public void ResolveReference_ShouldResolveByIdAndName()
    {
        _identity.RegisterNode(_node);
        var identifier = _identity.GetIdentifierOf(_node)!;

        Expect.Equal(identifier, _identity.ResolveReference(1, Core.Data.NetworkIdentityReference.OfId(identifier.LocalId)));
        Expect.Equal(identifier, _identity.ResolveReference(1, Core.Data.NetworkIdentityReference.OfFullName(identifier.FullName)));
        Expect.Null(_identity.ResolveReference(1, Core.Data.NetworkIdentityReference.OfFullName("Unknown Node")));
    }

    [Test]
    public void ReceivedIds_ShouldBeUsedForReferences()
    {
        _identity.RegisterNode(_node);
        var identifier = _identity.GetIdentifierOf(_node)!;

        // Peer 2 tells us that our node is #17 on their side
        var command = _commands.CommandsSent; // unused, receive path is exercised through the registered handler below
        var packet = IdentityPacketSerializer.Serialize(new Dictionary<string, int> { [identifier.FullName] = 17 });
        typeof(NetworkIdentityServer).GetMethod("HandleIds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(_identity, [2, packet]);

        Expect.True(identifier.ReferenceFor(2).HasId);
        Expect.Equal(17, identifier.ReferenceFor(2).Id);
        Expect.False(identifier.ReferenceFor(3).HasId);
    }
}
