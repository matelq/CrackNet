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

public partial class StateNode : Node, IRollbackTick, IRollbackSpawnAware, IRollbackDespawnAware, IRollbackDestroyAware
{
    public int TrackedValue { get; set; }
    public int Spawns { get; private set; }
    public int Despawns { get; private set; }
    public bool Destroyed { get; private set; }
    public bool Alive { get; private set; } = true;
    public int Ticks { get; private set; }

    public void RollbackTick(double delta, int tick, bool isFresh) => Ticks++;
    public void RollbackSpawn() { Spawns++; Alive = true; }
    public void RollbackDespawn() { Despawns++; Alive = false; }
    public void RollbackDestroy() => Destroyed = true;
}

public partial class NetworkHistoryServerTests : TestSuite
{
    private StateNode _subject = null!;

    public override async Task BeforeCase()
    {
        NetworkTime.Instance.SetTick(0);
        NetworkRollback.Instance.SetTick(0);
        _subject = await Mount(new StateNode { Name = "Subject" });
    }

    public override Task AfterCase()
    {
        NetworkHistoryServer.Instance.Deregister(_subject);
        RollbackLivenessServer.Instance.Deregister(_subject);
        return Task.CompletedTask;
    }

    [Test]
    public async Task MergeRollbackState_ShouldOverwriteAuthStateForSameTick()
    {
        var node = await Mount(new Node3D());
        var history = NetworkHistoryServer.Instance;
        var position = new NodePath("position");

        var baseline = Snapshot.Of(0, [(node, position, Vector3.Zero)], [node]);
        var initial = Snapshot.Of(1, [(node, position, Vector3.Zero)], [node]);
        var corrected = Snapshot.Of(1, [(node, position, Vector3.One)], [node]);

        history.MergeRollbackState(baseline);
        history.MergeRollbackState(initial);
        Expect.True(history.MergeRollbackState(corrected));

        var current = history.GetRollbackStateSnapshot(1)!;
        Expect.Equal(corrected, current);

        var diff = Snapshot.MakePatch(history.GetRollbackStateSnapshot(0)!, current);
        Expect.Equal(Snapshot.Of(1, [(node, position, Vector3.One)], [node]), diff);

        history.Deregister(node);
    }

    [Test]
    public void PushRollbackState_ShouldCreateSnapshotAtSpawnTick()
    {
        _subject.TrackedValue = 42;
        var history = NetworkHistoryServer.Instance;
        history.RegisterRollbackState(_subject, "TrackedValue");

        history.PushRollbackState(_subject, 5);

        var snapshot = history.GetRollbackStateSnapshot(5);
        Expect.NotNull(snapshot);
        Expect.True(snapshot!.HasProperty(_subject, "TrackedValue"));
        Expect.Equal(42, snapshot.GetProperty(_subject, "TrackedValue").AsInt32());
        Expect.Equal(5, history.GetLatestStateTickFor([_subject], 5));
    }

    [Test]
    public void PushRollbackState_ShouldMarkLocalAuthorityAsAuth()
    {
        var history = NetworkHistoryServer.Instance;
        history.RegisterRollbackState(_subject, "TrackedValue");
        history.PushRollbackState(_subject, 7);
        Expect.True(history.GetRollbackStateSnapshot(7)!.IsAuth(_subject));
    }

    [Test]
    public void PushRollbackState_ShouldMarkRemoteAuthorityAsNonAuth()
    {
        _subject.SetMultiplayerAuthority(2);
        var history = NetworkHistoryServer.Instance;
        history.RegisterRollbackState(_subject, "TrackedValue");
        history.PushRollbackState(_subject, 7);
        Expect.False(history.GetRollbackStateSnapshot(7)!.IsAuth(_subject));
    }

    [Test]
    public void PushRollbackState_ShouldNotCreateHistoryBeforeSpawnTick()
    {
        var history = NetworkHistoryServer.Instance;
        history.RegisterRollbackState(_subject, "TrackedValue");
        history.PushRollbackState(_subject, 6);
        Expect.Equal(-1, history.GetLatestStateTickFor([_subject], 5));
    }

    [Test]
    public void RecordRollbackState_ShouldSkipSubjectsNotAliveAtPreviousTick()
    {
        _subject.TrackedValue = 10;
        var history = NetworkHistoryServer.Instance;
        history.RegisterRollbackState(_subject, "TrackedValue");
        RollbackLivenessServer.Instance.Register(_subject, _subject.RollbackSpawn, _subject.RollbackDespawn, _subject.RollbackDestroy, 5);
        history.PushRollbackState(_subject, 5);
        _subject.TrackedValue = 99;

        history.RecordRollbackState(5);

        var spawnSnapshot = history.GetRollbackStateSnapshot(5)!;
        Expect.Equal(10, spawnSnapshot.GetProperty(_subject, "TrackedValue").AsInt32());
        Expect.Equal(5, history.GetLatestStateTickFor([_subject], 5));

        _subject.TrackedValue = 11;
        history.RecordRollbackState(6);

        var postSpawn = history.GetRollbackStateSnapshot(6)!;
        Expect.True(postSpawn.HasProperty(_subject, "TrackedValue"));
        Expect.Equal(11, postSpawn.GetProperty(_subject, "TrackedValue").AsInt32());
        Expect.Equal(6, history.GetLatestStateTickFor([_subject], 6));
    }

    [Test]
    public void MergeRollbackInput_ShouldNotLetPeersRewriteHistory()
    {
        var history = NetworkHistoryServer.Instance;
        var first = Snapshot.Of(3, [(_subject, "TrackedValue", 1)], [_subject]);
        var rewrite = Snapshot.Of(3, [(_subject, "TrackedValue", 2)], [_subject]);

        Expect.True(history.MergeRollbackInput(first));
        Expect.False(history.MergeRollbackInput(rewrite), "Re-sent input for a known subject should not count as new");
        Expect.Equal(1, history.GetRollbackInputSnapshot(3)!.GetProperty(_subject, "TrackedValue").AsInt32());
    }
}

public partial class RollbackLivenessServerTests : TestSuite
{
    private const int SpawnTick = 1;
    private const int DeathTick = 4;

    private RollbackLivenessServer _liveness = null!;
    private StateNode _subject = null!;

    public override async Task BeforeCase()
    {
        _liveness = await Mount(new RollbackLivenessServer());
        _subject = new StateNode();
    }

    public override Task AfterCase()
    {
        _subject.Free();
        return Task.CompletedTask;
    }

    private void RegisterSubject() => _liveness.Register(_subject, _subject.RollbackSpawn, _subject.RollbackDespawn, _subject.RollbackDestroy, SpawnTick);

    [Test]
    public void UnknownSubjectShouldBeAlive() => Expect.True(_liveness.IsAlive(_subject, SpawnTick));

    [Test]
    public void SubjectWithoutDespawn()
    {
        RegisterSubject();
        Expect.True(_liveness.IsAlive(_subject, SpawnTick));
        Expect.True(_liveness.IsAlive(_subject, SpawnTick + 1));
        Expect.False(_liveness.IsAlive(_subject, SpawnTick - 1));
    }

    [Test]
    public void SubjectWithSpawnAndDespawn()
    {
        RegisterSubject();
        _liveness.Despawn(_subject, DeathTick);
        Expect.True(_liveness.IsAlive(_subject, SpawnTick));
        Expect.False(_liveness.IsAlive(_subject, SpawnTick - 1));
        Expect.True(_liveness.IsAlive(_subject, DeathTick), "Alive on despawn tick");
        Expect.False(_liveness.IsAlive(_subject, DeathTick + 1), "Dead after despawn tick");
    }

    [Test]
    public void SingleTickSubject()
    {
        RegisterSubject();
        _liveness.Despawn(_subject, SpawnTick);
        Expect.True(_liveness.IsAlive(_subject, SpawnTick));
        Expect.False(_liveness.IsAlive(_subject, SpawnTick + 1));
        Expect.False(_liveness.IsAlive(_subject, SpawnTick - 1));
    }

    [Test]
    public void RestoreLiveness_ShouldDespawnAliveAndRespawnDead()
    {
        RegisterSubject();
        _liveness.Despawn(_subject, DeathTick);

        _liveness.RestoreLiveness(DeathTick + 1);
        Expect.False(_subject.Alive);
        Expect.Equal(1, _subject.Despawns);
        Expect.Equal(0, _subject.Spawns);

        _liveness.RestoreLiveness(DeathTick + 2);
        Expect.Equal(1, _subject.Despawns, "Should not despawn dead");

        _liveness.RestoreLiveness(DeathTick - 1);
        Expect.True(_subject.Alive);
        Expect.Equal(1, _subject.Spawns);
    }

    [Test]
    public void DestroyOldSubjects()
    {
        RegisterSubject();
        _liveness.DestroyOldSubjects(SpawnTick);
        Expect.False(_subject.Destroyed, "Should not free young subject");

        _liveness.Despawn(_subject, DeathTick);
        _liveness.DestroyOldSubjects(DeathTick + 1);
        Expect.True(_subject.Destroyed, "Should free old subject");
        Expect.False(_liveness.IsRegistered(_subject));
    }
}

public partial class RollbackSimulationServerTests : TestSuite
{
    private RollbackSimulationServer _simulation = null!;

    public override async Task BeforeCase()
    {
        _simulation = await Mount(new RollbackSimulationServer(NetworkHistoryServer.Instance, RollbackLivenessServer.Instance));
    }

    [Test]
    public async Task ShouldPredictNonOwnedNode()
    {
        var state = await Mount(new Node());
        var input = await Mount(new Node());
        var snapshot = Snapshot.Of(1, [(input, "name", "Input")]);
        state.SetMultiplayerAuthority(2);
        _simulation.RegisterRollbackInputFor(state, input);
        Expect.True(_simulation.IsPredicting(snapshot, state));
    }

    [Test]
    public async Task ShouldPredictOwnedNodeWithoutInput()
    {
        var state = await Mount(new Node());
        var input = await Mount(new Node());
        _simulation.RegisterRollbackInputFor(state, input);
        Expect.True(_simulation.IsPredicting(new Snapshot(0), state));
    }

    [Test]
    public async Task ShouldPredictNonOwnedInputless()
    {
        var state = await Mount(new Node());
        state.SetMultiplayerAuthority(2);
        Expect.True(_simulation.IsPredicting(new Snapshot(0), state));
    }

    [Test]
    public async Task ShouldNotPredictOwnedInputless()
    {
        var state = await Mount(new Node());
        Expect.False(_simulation.IsPredicting(new Snapshot(0), state));
    }

    [Test]
    public async Task ShouldNotPredictOwnedWithInput()
    {
        var state = await Mount(new Node());
        var input = await Mount(new Node());
        var snapshot = Snapshot.Of(1, [(input, "name", "Input")], [input]);
        _simulation.RegisterRollbackInputFor(state, input);
        Expect.False(_simulation.IsPredicting(snapshot, state));
    }

    [Test]
    public async Task ShouldNotSimulateWithoutInput()
    {
        var node = await Mount(new StateNode());
        var input = await Mount(new Node());
        _simulation.Register(node);
        _simulation.RegisterRollbackInputFor(node, input);
        Expect.Empty(_simulation.GetNodesToSimulate(new Snapshot(1)));
    }

    [Test]
    public async Task ShouldSimulateWithInput()
    {
        var node = await Mount(new StateNode());
        var input = await Mount(new Node());
        _simulation.Register(node);
        _simulation.RegisterRollbackInputFor(node, input);

        var snapshot = new Snapshot(1);
        snapshot.SetProperty(input, "editor_description", "Test input node");
        snapshot.SetAuth(input, true);

        Expect.SequenceEqual([node], _simulation.GetNodesToSimulate(snapshot));
    }

    [Test]
    public async Task ShouldSimulateMutated()
    {
        var node = await Mount(new StateNode());
        var input = await Mount(new Node());
        _simulation.Register(node);
        _simulation.RegisterRollbackInputFor(node, input);
        NetworkRollback.Instance.Mutate(node, 1);

        Expect.SequenceEqual([node], _simulation.GetNodesToSimulate(new Snapshot(1)));
    }

    [Test]
    public async Task Simulate_ShouldCallCallbackAndTrackFreshness()
    {
        var node = await Mount(new StateNode());
        _simulation.Register(node);
        var history = NetworkHistoryServer.Instance;
        history.MergeRollbackInput(new Snapshot(3));

        var freshness = new List<bool>();
        _simulation.Deregister(node);
        _simulation.Register(node, (_, _, fresh) => freshness.Add(fresh));

        _simulation.Simulate(0.1, 3);
        _simulation.Simulate(0.1, 3);
        Expect.SequenceEqual([true, false], freshness);

        _simulation.TrimTicksSimulated(4);
        _simulation.Simulate(0.1, 3);
        Expect.SequenceEqual([true, false, true], freshness);
    }
}

public partial class NetworkSynchronizationServerTests : TestSuite
{
    private TestingCommandServer _commands = null!;
    private NetworkSynchronizationServer _sync = null!;

    public override async Task BeforeCase()
    {
        _commands = await Mount(new TestingCommandServer());
        _sync = await Mount(new NetworkSynchronizationServer(_commands, NetworkHistoryServer.Instance, NetworkIdentityServer.Instance, RollbackSimulationServer.Instance));
        _sync.EnableInputBroadcast = true;
    }

    [Test]
    public void MakePatch_ShouldIncludeNewlyAuthoritativeSubjects()
    {
        var host = new Node();
        var remote = new Node();
        var position = new NodePath("position");

        var reference = new Snapshot(100);
        reference.SetProperty(host, position, Vector3.Zero);
        reference.SetAuth(host, true);

        var resimulated = new Snapshot(100);
        resimulated.SetProperty(host, position, new Vector3(1, 0, 0));
        resimulated.SetAuth(host, true);
        resimulated.SetProperty(remote, position, new Vector3(2, 0, 0));
        resimulated.SetAuth(remote, true);

        var diff = Snapshot.MakePatch(reference, resimulated);
        Expect.True(diff.HasProperty(remote, position));
        Expect.VariantEqual(new Vector3(2, 0, 0), diff.GetProperty(remote, position));

        host.Free();
        remote.Free();
    }

    [Test]
    public async Task MakePeerSnapshot_ShouldIncludeOnlyVisibleOwnedAuthState()
    {
        var visible = await Mount(new Node3D { Name = "visible", Position = Vector3.One });
        var hidden = await Mount(new Node3D { Name = "hidden", Position = Vector3.Up });
        var remote = await Mount(new Node3D { Name = "remote", Position = Vector3.Right });
        remote.SetMultiplayerAuthority(2);

        _sync.RegisterRollbackState(visible, "position");
        _sync.RegisterRollbackState(hidden, "position");
        _sync.RegisterRollbackState(remote, "position");

        var filter = new PeerVisibilityFilter { DefaultVisibility = false };
        filter.SetVisibilityFor(2, true);
        filter.UpdateVisibility([2, 3]);
        _sync.RegisterVisibilityFilter(visible, filter);

        var hiddenFilter = new PeerVisibilityFilter { DefaultVisibility = false };
        hiddenFilter.UpdateVisibility([2, 3]);
        _sync.RegisterVisibilityFilter(hidden, hiddenFilter);

        var snapshot = Snapshot.Of(5, [
            (visible, "position", visible.Position),
            (hidden, "position", hidden.Position),
            (remote, "position", remote.Position),
        ], [visible, hidden, remote]);

        var peerSnapshot = _sync.MakePeerSnapshot(snapshot, 2, _sync.OwnedRollbackStateProperties);

        Expect.True(peerSnapshot.HasProperty(visible, "position"));
        Expect.VariantEqual(Vector3.One, peerSnapshot.GetProperty(visible, "position"));
        Expect.True(peerSnapshot.IsAuth(visible));
        Expect.False(peerSnapshot.HasSubject(hidden));
        Expect.False(peerSnapshot.HasSubject(remote));

        filter.Free();
        hiddenFilter.Free();
    }

    [Test]
    public async Task ShouldRememberRollbackStateSeparatelyPerPeer()
    {
        var node = await Mount(new Node3D { Name = "subject" });
        var tick5 = Snapshot.Of(5, [(node, "position", Vector3.One)], [node]);
        var tick8 = Snapshot.Of(8, [(node, "position", Vector3.Up)], [node]);

        _sync.RememberSentRollbackState(2, tick5);
        _sync.RememberSentRollbackState(3, tick8);

        var peer2 = _sync.GetLastSentRollbackState(2, 8);
        var peer3 = _sync.GetLastSentRollbackState(3, 8);
        Expect.NotNull(peer2);
        Expect.NotNull(peer3);
        Expect.Equal(5, peer2!.Tick);
        Expect.VariantEqual(Vector3.One, peer2.GetProperty(node, "position"));
        Expect.Equal(8, peer3!.Tick);
        Expect.VariantEqual(Vector3.Up, peer3.GetProperty(node, "position"));
        Expect.Null(_sync.GetLastSentRollbackState(4, 8));
    }

    [Test]
    public async Task ShouldDuplicateRememberedRollbackState()
    {
        var node = await Mount(new Node3D { Name = "subject" });
        var snapshot = Snapshot.Of(5, [(node, "position", Vector3.One)], [node]);

        _sync.RememberSentRollbackState(2, snapshot);
        snapshot.SetProperty(node, "position", Vector3.Zero);

        var remembered = _sync.GetLastSentRollbackState(2, 5);
        Expect.NotNull(remembered);
        Expect.VariantEqual(Vector3.One, remembered!.GetProperty(node, "position"));
    }

    [Test]
    public async Task SynchronizeInput_ShouldSendOwnedInputToPeers()
    {
        var owned = await Mount(new Node3D { Name = "owned" });
        NetworkIdentityServer.Instance.RegisterNode(owned);
        var history = NetworkHistoryServer.Instance;

        history.RegisterRollbackInput(owned, "position");
        _sync.RegisterRollbackInput(owned, "position");

        history.RecordRollbackInput(0);
        // Offline peer has no remote peers; simulate a recipient by forcing the peer list through the history snapshot
        _sync.SynchronizeInput(0);
        Expect.Empty(_commands.CommandsSent.Where(c => c.Idx == CommandIds.Input), "No peers connected, nothing should be sent");

        NetworkIdentityServer.Instance.DeregisterNode(owned);
        history.Deregister(owned);
    }
}

public partial class NetworkRollbackTests : TestSuite
{
    private NetworkRollback _rollback = null!;
    private Node _node = null!;

    public override async Task BeforeCase()
    {
        _rollback = await Mount(new NetworkRollback());
        _node = new Node();
    }

    public override Task AfterCase()
    {
        _node.Free();
        return Task.CompletedTask;
    }

    [Test]
    public void ShouldBeMutatedAfter()
    {
        _rollback.Mutate(_node, 8);
        Expect.True(_rollback.IsMutated(_node, 10));
        Expect.False(_rollback.IsJustMutated(_node, 10));
    }

    [Test]
    public void ShouldJustBeMutated()
    {
        _rollback.Mutate(_node, 8);
        Expect.True(_rollback.IsMutated(_node, 8));
        Expect.True(_rollback.IsJustMutated(_node, 8));
    }

    [Test]
    public void ShouldNotBeMutatedBefore()
    {
        _rollback.Mutate(_node, 8);
        Expect.False(_rollback.IsMutated(_node, 4));
        Expect.False(_rollback.IsJustMutated(_node, 4));
    }

    [Test]
    public void UnknownShouldNotBeMutated()
    {
        Expect.False(_rollback.IsMutated(_node, 8));
        Expect.False(_rollback.IsJustMutated(_node, 8));
    }

    [Test]
    public void Mutate_ShouldKeepEarliestTick()
    {
        _rollback.Mutate(_node, 8);
        _rollback.Mutate(_node, 5);
        _rollback.Mutate(_node, 12);
        Expect.True(_rollback.IsJustMutated(_node, 5));
    }
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
        Expect.Equal(_node.GetPath().ToString(), identifier!.FullName);
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
