using Netfox.Core.Data;

namespace Netfox.Core.Tests.Data;

public class PerObjectHistoryTests
{
    private sealed class Subject;

    [Fact]
    public void EnsureSnapshot_ShouldCarryForwardLatest()
    {
        var subject = new Subject();
        var history = new PerObjectHistory<Subject, string, object>(8);
        history.SetProperty(2, subject, "hp", 100);

        var snapshot = history.EnsureSnapshot(5, subject, carryForward: true)!;
        Assert.Equal(100, snapshot.GetValue("hp"));
        Assert.Equal(5, history.GetLatestTick(6, subject));
        Assert.Equal(2, history.GetLatestTick(4, subject));
    }

    [Fact]
    public void EnsureSnapshot_ShouldCreateEmptyWithoutCarry()
    {
        var subject = new Subject();
        var history = new PerObjectHistory<Subject, string, object>(8);
        history.SetProperty(2, subject, "hp", 100);

        var snapshot = history.EnsureSnapshot(5, subject, carryForward: false)!;
        Assert.False(snapshot.HasValue("hp"));
        Assert.Null(history.GetLatestSnapshot(1, subject));
        Assert.Equal(-1, history.GetLatestTick(1, subject));
    }
}
