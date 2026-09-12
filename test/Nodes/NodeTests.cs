using Godot;
using Netfox.Core.Serialization;
using Netfox.Extras;

namespace Netfox.Tests;

public partial class RollbackAwareNode : Node, IRollbackTick
{
    public void RollbackTick(double delta, int tick, bool isFresh) { }
    public override string ToString() => "RollbackAware:" + Name;
}

public partial class RollbackSynchronizerTests : TestSuite
{
    public override Task BeforeCase()
    {
        NetworkTime.Instance.SetTick(0);
        NetworkRollback.Instance.SetTick(0);
        return Task.CompletedTask;
    }

    public override Task AfterCase()
    {
        NetworkRollback.Instance.NotifyResimulationStart(NetworkTime.Instance.Tick);
        return Task.CompletedTask;
    }

    [Test]
    public async Task NestedRollbackSynchronizerSupport()
    {
        // primary_root/ (primary_rbs, secondary_root/ (secondary_rbs))
        var primaryRoot = new RollbackAwareNode { Name = "Primary Root" };
        var primaryRbs = new RollbackSynchronizer { Name = "Primary RBS" };
        var secondaryRoot = new RollbackAwareNode { Name = "Secondary Root" };
        var secondaryRbs = new RollbackSynchronizer { Name = "Secondary RBS" };

        primaryRoot.AddChild(primaryRbs);
        primaryRoot.AddChild(secondaryRoot);
        secondaryRoot.AddChild(secondaryRbs);
        primaryRbs.Root = primaryRoot;
        secondaryRbs.Root = secondaryRoot;

        await Mount(primaryRoot);

        primaryRbs.ProcessSettings();
        secondaryRbs.ProcessSettings();

        Expect.SequenceEqual([primaryRoot], primaryRbs.SimulatedNodes);
        Expect.SequenceEqual([secondaryRoot], secondaryRbs.SimulatedNodes);
    }

    private async Task<(StateNode Root, RollbackSynchronizer Rbs)> CreateSpawnSynchronizer(int spawnTick, int trackedValue)
    {
        var root = new StateNode { Name = "Spawn Root", TrackedValue = trackedValue };
        var rbs = new RollbackSynchronizer
        {
            Name = "Spawn RBS",
            Root = root,
            StateProperties = [":TrackedValue"],
            SpawnTick = spawnTick,
        };
        root.AddChild(rbs);
        await Mount(root);
        return (root, rbs);
    }

    [Test]
    public async Task ShouldSeedInitialRollbackStateAtSpawnTick()
    {
        var (root, rbs) = await CreateSpawnSynchronizer(4, 21);
        rbs.ProcessAuthority();

        var snapshot = NetworkHistoryServer.Instance.GetRollbackStateSnapshot(4);
        Expect.NotNull(snapshot);
        Expect.True(snapshot!.HasProperty(root, "TrackedValue"));
        Expect.Equal(21, snapshot.GetProperty(root, "TrackedValue").AsInt32());
        Expect.Equal(4, NetworkHistoryServer.Instance.GetLatestStateTickFor([root], 4));
    }

    [Test]
    public async Task ShouldRegisterLivenessUsingSpawnTick()
    {
        var (root, rbs) = await CreateSpawnSynchronizer(6, 0);
        rbs.ProcessSettings();
        Expect.True(RollbackLivenessServer.Instance.IsAlive(root, 6));
        Expect.False(RollbackLivenessServer.Instance.IsAlive(root, 5));
    }

    [Test]
    public async Task ShouldReseedStateAndClearDespawnWhenSpawned()
    {
        var (root, rbs) = await CreateSpawnSynchronizer(2, 21);
        rbs.ProcessSettings();

        rbs.Despawn(5);
        root.TrackedValue = 42;
        rbs.Spawn(7);

        var snapshot = NetworkHistoryServer.Instance.GetRollbackStateSnapshot(7);
        Expect.Equal(7, rbs.SpawnTick);
        Expect.True(RollbackLivenessServer.Instance.IsAlive(root, 8));
        Expect.NotNull(snapshot);
        Expect.Equal(42, snapshot!.GetProperty(root, "TrackedValue").AsInt32());
    }

    [Test]
    public async Task ShouldRequestResimulationFromSpawnTick()
    {
        NetworkTime.Instance.SetTick(12);
        await CreateSpawnSynchronizer(3, 0);

        // Run the rollback loop: BeforeLoop asks the synchronizer, which requests its spawn tick
        var prepared = new List<int>();
        void OnPrepare(int tick) => prepared.Add(tick);
        NetworkRollback.Instance.OnPrepareTick += OnPrepare;
        NetworkRollback.Instance.Rollback();
        NetworkRollback.Instance.OnPrepareTick -= OnPrepare;

        Expect.True(prepared.Count > 0, "Rollback should have run ticks");
        Expect.Equal(3, prepared[0]);
        Expect.Equal(11, prepared[^1]);
    }

    [Test]
    public async Task ExitTree_ShouldDeregisterEverything()
    {
        var (root, rbs) = await CreateSpawnSynchronizer(2, 1);
        rbs.ProcessSettings();
        Expect.True(RollbackLivenessServer.Instance.IsRegistered(root));

        RemoveChild(root);
        root.Free();
        await NextFrame();

        Expect.False(RollbackLivenessServer.Instance.IsRegistered(root));
    }
}

public partial class PredictiveSynchronizerTests : TestSuite
{
    private StateNode _root = null!;
    private PredictiveSynchronizer _synchronizer = null!;

    public override async Task BeforeCase()
    {
        NetworkTime.Instance.SetTick(10);
        NetworkRollback.Instance.SetTick(10);
        _root = new StateNode { Name = "Predictive Root" };
        _synchronizer = new PredictiveSynchronizer { Name = "PredictiveSynchronizer", Root = _root, StateProperties = [":TrackedValue"] };
        _root.AddChild(_synchronizer);
        await Mount(_root);
    }

    [Test]
    public void ShouldSeedRollbackStateAtSpawnTick()
    {
        _root.TrackedValue = 13;
        _synchronizer.SpawnTick = 4;
        _synchronizer.ProcessSettings();

        var snapshot = NetworkHistoryServer.Instance.GetRollbackStateSnapshot(4);
        Expect.NotNull(snapshot);
        Expect.Equal(13, snapshot!.GetProperty(_root, "TrackedValue").AsInt32());
        Expect.Equal(4, NetworkHistoryServer.Instance.GetLatestStateTickFor([_root], 4));
    }

    [Test]
    public void ShouldRegisterLivenessUsingSpawnTick()
    {
        _synchronizer.SpawnTick = 6;
        _synchronizer.ProcessSettings();
        Expect.True(RollbackLivenessServer.Instance.IsAlive(_root, 6));
        Expect.False(RollbackLivenessServer.Instance.IsAlive(_root, 5));
    }

    [Test]
    public void ShouldReseedStateAndClearDespawnWhenSpawned()
    {
        _synchronizer.ProcessSettings();
        _synchronizer.Despawn(5);
        _root.TrackedValue = 42;
        _synchronizer.Spawn(7);

        var snapshot = NetworkHistoryServer.Instance.GetRollbackStateSnapshot(7);
        Expect.Equal(7, _synchronizer.SpawnTick);
        Expect.True(RollbackLivenessServer.Instance.IsAlive(_root, 8));
        Expect.NotNull(snapshot);
        Expect.Equal(42, snapshot!.GetProperty(_root, "TrackedValue").AsInt32());
    }
}

public partial class RewindableActionTests : TestSuite
{
    [Test]
    public async Task ShouldEmptyQueueAfterLoop()
    {
        var action = await Mount(new RewindableAction());
        action.SetMultiplayerAuthority(2); // run as client

        var data = TicksetSerializer.Serialize(0, 4, [0]);
        NetworkTime.Instance.SetTick(4);
        NetworkRollback.Instance.SetTick(4);

        action.ReceiveState(data);
        NetworkTime.Instance.RunBeforeTickLoop();
        NetworkTime.Instance.RunAfterTickLoop();
        Expect.True(action.HasConfirmed(), "RewindableAction was not confirmed!");
        Expect.True(action.IsActive(0));

        action.ReceiveState(data);
        NetworkTime.Instance.RunBeforeTickLoop();
        NetworkTime.Instance.RunAfterTickLoop();
        Expect.False(action.HasConfirmed(), "RewindableAction was redundantly confirmed!");
    }

    [Test]
    public async Task Status_ShouldFollowLocalChanges()
    {
        var action = await Mount(new RewindableAction());
        NetworkTime.Instance.SetTick(8);
        NetworkRollback.Instance.SetTick(7);

        Expect.Equal(RewindableAction.Status.Inactive, action.GetStatus(7));
        action.SetActive(true, 7);
        Expect.Equal(RewindableAction.Status.Confirming, action.GetStatus(7));

        // The rollback loop moves the rollback tick, so query the tick explicitly afterwards
        NetworkTime.Instance.RunBeforeTickLoop();
        NetworkTime.Instance.RunAfterTickLoop();
        Expect.Equal(RewindableAction.Status.Active, action.GetStatus(7));
        Expect.True(action.HasConfirmed());

        action.SetActive(false, 7);
        Expect.Equal(RewindableAction.Status.Cancelling, action.GetStatus(7));
    }

    [Test]
    public async Task Context_ShouldBeStoredPerTick()
    {
        var action = await Mount(new RewindableAction());
        action.SetContext("hello", 3);
        Expect.True(action.HasContext(3));
        Expect.Equal("hello", action.GetContext<string>(3));
        Expect.False(action.HasContext(4));
        action.EraseContext(3);
        Expect.False(action.HasContext(3));
    }
}

public partial class InterpolationServerTests : TestSuite
{
    private InterpolationServer _server = null!;
    private Node3D _node = null!;

    public override async Task BeforeCase()
    {
        _server = await Mount(new InterpolationServer());
        _node = await Mount(new Node3D { Name = "TestNode" });
        _server.SetServerEnabled(true);
    }

    [Test]
    public void Register_ShouldRegisterSubjectWithDefaults()
    {
        _server.Register(_node, "position");
        _server.Register(_node, "rotation");
        _server.Register(_node, "position");
        Expect.True(_server.HasSubject(_node));
        Expect.True(_server.IsEnabled(_node));
        Expect.True(_server.IsRecording(_node));
    }

    [Test]
    public void Deregister_ShouldRemoveSubject()
    {
        _server.Register(_node, "position");
        _server.Deregister(_node);
        Expect.False(_server.HasSubject(_node));
        Expect.False(_server.IsEnabled(_node));
        Expect.False(_server.IsRecording(_node));
        Expect.False(_server.CanInterpolate(_node));
        _server.Deregister(_node);
    }

    [Test]
    public void SetEnabledAndRecording_ShouldToggle()
    {
        _server.Register(_node, "position");
        _server.SetEnabled(_node, false);
        Expect.False(_server.CanInterpolate(_node));
        _server.SetEnabled(_node, true);
        Expect.True(_server.CanInterpolate(_node));

        _server.SetRecording(_node, false);
        Expect.False(_server.IsRecording(_node));
        Expect.True(_server.CanInterpolate(_node));
    }

    [Test]
    public void CanInterpolate_ShouldBeFalseWhenUnknownOrTeleporting()
    {
        Expect.False(_server.CanInterpolate(_node));
        _server.Register(_node, "position");
        _server.Teleport(_node);
        Expect.False(_server.CanInterpolate(_node));
        _server.ClearTeleports();
        Expect.True(_server.CanInterpolate(_node));
    }

    [Test]
    public void PushState_ShouldRotateStates()
    {
        _node.Position = Vector3.Zero;
        _server.Register(_node, "position");

        _server.PushState(_node);
        _server.InterpolateSubject(_node, 1.0);
        var firstTo = _node.Position;

        _node.Position = new Vector3(10, 0, 0);
        _server.PushState(_node);

        _server.InterpolateSubject(_node, 0.0);
        Expect.Equal(firstTo, _node.Position);
        _server.InterpolateSubject(_node, 1.0);
        Expect.Equal(new Vector3(10, 0, 0), _node.Position);
    }

    [Test]
    public void InterpolateSubject_ShouldInterpolateMultipleProperties()
    {
        _server.Register(_node, "position");
        _server.Register(_node, "rotation");

        _node.Position = Vector3.Zero;
        _node.Rotation = Vector3.Zero;
        _server.PushState(_node);

        _node.Position = new Vector3(10, 10, 10);
        _node.Rotation = new Vector3(Mathf.Pi, 0, 0);
        _server.PushState(_node);
        _server.InterpolateSubject(_node, 0.25);

        Expect.True(_node.Position.IsEqualApprox(new Vector3(2.5f, 2.5f, 2.5f)), $"Position was {_node.Position}");
        Expect.Approx(Mathf.Pi * 0.25, _node.Rotation.X, 0.01);
    }

    [Test]
    public void InterpolateSubject_ShouldNotInterpolateWhenDisabled()
    {
        _server.Register(_node, "position");
        _server.SetEnabled(_node, false);
        _node.Position = Vector3.Zero;
        _server.PushState(_node);
        _node.Position = new Vector3(10, 0, 0);
        _server.PushState(_node);
        _server.InterpolateSubject(_node, 0.5);
        Expect.Equal(new Vector3(10, 0, 0), _node.Position);
    }
}

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

public partial class RewindableStateMachineTests : TestSuite
{
    private partial class RecordingState : RewindableState
    {
        public readonly List<string> Calls = new();
        public bool AllowEnter = true;

        public override void Tick(double delta, int tick, bool isFresh) => Calls.Add(FormattableString.Invariant($"tick {delta} {tick} {isFresh}"));
        public override void Enter(RewindableState? previousState, int tick) => Calls.Add($"enter {previousState?.Name ?? "null"} {tick}");
        public override void Exit(RewindableState nextState, int tick) => Calls.Add($"exit {nextState.Name} {tick}");
        public override bool CanEnter(RewindableState? previousState) => AllowEnter;
        public override void DisplayEnter(RewindableState? previousState, int tick) => Calls.Add("display_enter");
        public override void DisplayExit(RewindableState nextState, int tick) => Calls.Add("display_exit");
    }

    private RewindableStateMachine _machine = null!;
    private RecordingState _first = null!;
    private RecordingState _other = null!;

    public override async Task BeforeCase()
    {
        NetworkRollback.Instance.SetTick(0);
        _machine = new RewindableStateMachine();
        _first = new RecordingState { Name = "First State" };
        _other = new RecordingState { Name = "Other State" };
        _machine.AddChild(_first);
        _machine.AddChild(_other);
        await Mount(_machine);
    }

    [Test]
    public void ShouldStartEmpty() => Expect.Equal("", _machine.State.ToString());

    [Test]
    public void ShouldNotifyNewStateOnEnter()
    {
        var entered = false;
        _first.OnEnter += (_, _, _) => entered = true;
        Expect.True(_machine.Transition("First State"));
        Expect.True(entered);
        Expect.SequenceEqual(["enter null 0"], _first.Calls);
        Expect.Equal("First State", _machine.State.ToString());
    }

    [Test]
    public void OnEnterShouldPreventTransition()
    {
        _other.OnEnter += (_, _, prevent) => prevent();
        _machine.Transition("First State");
        Expect.False(_machine.Transition("Other State"));
        Expect.Equal("First State", _machine.State.ToString());
    }

    [Test]
    public void OnExitShouldPreventTransition()
    {
        _first.OnExit += (_, _, prevent) => prevent();
        _machine.Transition("First State");
        Expect.False(_machine.Transition("Other State"));
        Expect.Equal("First State", _machine.State.ToString());
    }

    [Test]
    public void CanEnterShouldPreventTransition()
    {
        _machine.State = "First State";
        _other.AllowEnter = false;
        Expect.False(_machine.Transition("Other State"));
        Expect.Equal("First State", _machine.State.ToString());
    }

    [Test]
    public void ShouldCallTick()
    {
        var ticks = new List<(double, int, bool)>();
        _first.OnTick += (d, t, f) => ticks.Add((d, t, f));
        _machine.Transition("First State");
        _machine.RollbackTick(0.16, 0, true);
        Expect.SequenceEqual(["enter null 0", "tick 0.16 0 True"], _first.Calls);
        Expect.SequenceEqual([(0.16, 0, true)], ticks);
    }

    [Test]
    public void ShouldNotifyDisplayState()
    {
        _machine.State = "First State";

        NetworkTime.Instance.RunBeforeTickLoop();
        _machine.Transition("First State");
        NetworkTime.Instance.RunAfterTickLoop();
        Expect.True(_first.Calls.Contains("display_enter"));

        NetworkTime.Instance.RunBeforeTickLoop();
        _machine.Transition("Other State");
        NetworkTime.Instance.RunAfterTickLoop();
        Expect.True(_first.Calls.Contains("display_exit"));
        Expect.True(_other.Calls.Contains("display_enter"));
    }
}

public partial class RewindableRandomNumberGeneratorTests : TestSuite
{
    private const int SomeSeed = 3079;
    private const int OtherSeed = 9875;

    private static List<int> Batch(RewindableRandomNumberGenerator rng) => Enumerable.Range(0, 4).Select(_ => rng.RandiRange(0, 10)).ToList();

    public override Task AfterCase()
    {
        NetworkRollback.Instance.SetIsRollback(false);
        return Task.CompletedTask;
    }

    [Test]
    public void ShouldGenerateSameNumbersForSameRollbackTickInDifferentLoop()
    {
        NetworkRollback.Instance.SetIsRollback(true);
        var rng = new RewindableRandomNumberGenerator(SomeSeed);
        NetworkTime.Instance.SetTick(2); NetworkRollback.Instance.SetTick(4);
        var first = Batch(rng);
        NetworkTime.Instance.SetTick(3); NetworkRollback.Instance.SetTick(4);
        var second = Batch(rng);
        Expect.SequenceEqual(first, second);
    }

    [Test]
    public void ShouldGenerateDifferentNumbersForDifferentTicks()
    {
        NetworkRollback.Instance.SetIsRollback(true);
        var rng = new RewindableRandomNumberGenerator(SomeSeed);
        NetworkTime.Instance.SetTick(2); NetworkRollback.Instance.SetTick(4);
        var first = Batch(rng);
        NetworkTime.Instance.SetTick(2); NetworkRollback.Instance.SetTick(2);
        var second = Batch(rng);
        Expect.False(first.SequenceEqual(second));
    }

    [Test]
    public void DifferentSeedsShouldGenerateDifferentNumbers()
    {
        NetworkRollback.Instance.SetIsRollback(true);
        NetworkTime.Instance.SetTick(0); NetworkRollback.Instance.SetTick(0);
        var first = Batch(new RewindableRandomNumberGenerator(SomeSeed));
        var second = Batch(new RewindableRandomNumberGenerator(OtherSeed));
        Expect.False(first.SequenceEqual(second));
    }

    [Test]
    public void RandfRange_ShouldNotNeedWarmup()
    {
        var rng = new RewindableRandomNumberGenerator(SomeSeed);
        var value = rng.RandfRange(-1f, 1f);
        Expect.NotEqual(-1f, value);
        Expect.NotEqual(1f, value);
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
