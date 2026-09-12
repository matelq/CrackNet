using Godot;
using Netfox.Examples.Snippets;

namespace Netfox.Tests;

/// <summary>Port of test/examples/snippets/input-gathering-tutorial/one-off-input.test.gd.</summary>
public partial class OneOffInputTests : TestSuite
{
    private const string Action = "netfox_test_jump";

    private OneOffInput _input = null!;

    public override async Task BeforeCase()
    {
        if (!InputMap.HasAction(Action)) InputMap.AddAction(Action);

        NetworkTime.Instance.SetTick(0);
        _input = await Mount(new OneOffInput { Name = "One Off Input", Action = Action });
    }

    public override Task AfterCase()
    {
        Input.ActionRelease(Action);
        InputMap.EraseAction(Action);
        FreeChildren();
        return Task.CompletedTask;
    }

    /// <summary>Presses and releases within one frame, as a player tapping a key between two ticks would.</summary>
    private void TapAction()
    {
        Input.ActionPress(Action);
        _input._Process(1.0 / 60.0);
        Input.ActionRelease(Action);
    }

    [Test]
    public void ShouldBeFalseWithoutInput()
    {
        Expect.False(_input.IsJumping);

        NetworkTime.Instance.RunTick();
        Expect.False(_input.IsJumping);
    }

    [Test]
    public void ShouldBeTrueOnTheTickAfterThePress()
    {
        TapAction();

        NetworkTime.Instance.RunTick();
        Expect.True(_input.IsJumping, "the tick after the press should see the input");
    }

    [Test]
    public void ShouldBeTrueOnlyOnTheFirstTick()
    {
        TapAction();

        NetworkTime.Instance.RunTick();
        Expect.True(_input.IsJumping, "first tick should have input");

        NetworkTime.Instance.RunTick();
        Expect.False(_input.IsJumping, "second tick should not have input");
    }

    [Test]
    public void ShouldNotStackUpAcrossFrames()
    {
        // Held across several frames, the press is still a single tick of input
        Input.ActionPress(Action);
        _input._Process(1.0 / 60.0);
        _input._Process(1.0 / 60.0);
        Input.ActionRelease(Action);

        NetworkTime.Instance.RunTick();
        Expect.True(_input.IsJumping);

        NetworkTime.Instance.RunTick();
        Expect.False(_input.IsJumping);
    }
}
