namespace Netfox.Tests;

/// <summary>
/// The cases upstream left as todo() in test/netfox/rollback-synchronizer.test.gd ("Messy to set up, keeping cases for
/// later"): input age, last known input and last known state. Input history is fed directly through the history server.
/// </summary>
public partial class RollbackSynchronizerInputAgeTests : TestSuite
{
    private StateNode _root = null!;
    private StateNode _input = null!;
    private RollbackSynchronizer _rbs = null!;

    public override async Task BeforeCase()
    {
        NetworkTime.Instance.SetTick(0);
        NetworkRollback.Instance.SetTick(0);

        _root = new StateNode { Name = "Input Age Root" };
        _input = new StateNode { Name = "Input" };
        _root.AddChild(_input);

        _rbs = new RollbackSynchronizer
        {
            Name = "Input Age RBS",
            Root = _root,
            StateProperties = [":TrackedValue"],
            InputProperties = ["Input:TrackedValue"],
        };
        _root.AddChild(_rbs);

        await Mount(_root);
        _rbs.ProcessSettings();
    }

    public override Task AfterCase()
    {
        FreeChildren();
        return Task.CompletedTask;
    }

    private void RecordInput(int tick)
    {
        _input.TrackedValue = tick;
        NetworkHistoryServer.Instance.RecordRollbackInput(tick);
    }

    [Test]
    public void InputAge_ShouldBeMinusOneWithoutInput()
    {
        Expect.Equal(-1, _rbs.GetInputAge());
        Expect.False(_rbs.HasInput());
    }

    [Test]
    public void InputAge_ShouldBeZeroForInputOnTheCurrentTick()
    {
        RecordInput(5);
        NetworkRollback.Instance.SetTick(5);

        Expect.Equal(0, _rbs.GetInputAge());
        Expect.True(_rbs.HasInput());
    }

    [Test]
    public void InputAge_ShouldGrowWithOlderInput()
    {
        RecordInput(5);
        NetworkRollback.Instance.SetTick(8);

        Expect.Equal(3, _rbs.GetInputAge());
        Expect.True(_rbs.HasInput());
    }

    [Test]
    public void LastKnownInput_ShouldBeMinusOneWithoutInput()
    {
        NetworkTime.Instance.SetTick(4);
        Expect.Equal(-1, _rbs.GetLastKnownInput());
    }

    [Test]
    public void LastKnownInput_ShouldBeTheLatestRecordedTick()
    {
        RecordInput(5);
        RecordInput(9);
        NetworkTime.Instance.SetTick(12);

        Expect.Equal(9, _rbs.GetLastKnownInput());
    }

    [Test]
    public async Task LastKnownState_ShouldBeMinusOneBeforeSettingsAreProcessed()
    {
        // ProcessSettings seeds state at the spawn tick, so "no state at all" only exists before that
        var root = new StateNode { Name = "Unprocessed Root" };
        var rbs = new RollbackSynchronizer { Name = "Unprocessed RBS", Root = root, StateProperties = [":TrackedValue"] };
        root.AddChild(rbs);
        await Mount(root);

        NetworkTime.Instance.SetTick(4);

        // Despite its name, this returns the age of the latest state, as upstream does (rollback-synchronizer.gd)
        Expect.Equal(-1, rbs.GetLastKnownState());
    }

    [Test]
    public void LastKnownState_ShouldCountFromTheSeededSpawnState()
    {
        // The synchronizer seeded state at its spawn tick, which defaults to the rollback tick plus one
        Expect.Equal(1, _rbs.SpawnTick);
        NetworkTime.Instance.SetTick(4);

        Expect.Equal(3, _rbs.GetLastKnownState());
    }

    [Test]
    public void LastKnownState_ShouldBeTheAgeOfTheLatestState()
    {
        _root.TrackedValue = 3;
        NetworkHistoryServer.Instance.PushRollbackState(_root, 6);
        NetworkTime.Instance.SetTick(10);

        Expect.Equal(4, _rbs.GetLastKnownState());
    }
}
