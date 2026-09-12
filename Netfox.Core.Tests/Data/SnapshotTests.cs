using Netfox.Core.Data;

namespace Netfox.Core.Tests.Data;

public class SnapshotTests
{
    private sealed class Subject { public override string ToString() => "Subject"; }

    private static Snapshot<Subject, string, object> Of(int tick, (Subject, string, object)[] entries, params Subject[] auth)
        => Snapshot<Subject, string, object>.Of(tick, entries, auth);

    private readonly Subject _node = new();
    private readonly Subject _other = new();

    [Fact]
    public void MakePatch_ShouldReturnEmptyOnSame()
    {
        var snapshot = Of(0, [(_node, "position", 0), (_node, "scale", 1)], _node);
        Assert.True(Snapshot<Subject, string, object>.MakePatch(snapshot, snapshot).IsEmpty);
    }

    [Fact]
    public void MakePatch_ShouldIncludeDifferingProperty()
    {
        var from = Of(0, [(_node, "position", 0), (_node, "scale", 1)], _node);
        var to = Of(0, [(_node, "position", 1), (_node, "scale", 1)], _node);
        var expected = Of(0, [(_node, "position", 1)], _node);
        Assert.Equal(expected, Snapshot<Subject, string, object>.MakePatch(from, to));
    }

    [Fact]
    public void MakePatch_ShouldIncludeNewProperty()
    {
        var from = Of(0, [(_node, "position", 0)], _node);
        var to = Of(0, [(_node, "position", 0), (_node, "scale", 1)], _node);
        var expected = Of(0, [(_node, "scale", 1)], _node);
        Assert.Equal(expected, Snapshot<Subject, string, object>.MakePatch(from, to));
    }

    [Fact]
    public void MakePatch_PatchShouldYieldToOnMerge()
    {
        var from = Of(0, [(_node, "position", 0), (_node, "scale", 1)], _node);
        var to = Of(0, [(_node, "position", 1), (_node, "scale", 1)], _node);
        var patch = Snapshot<Subject, string, object>.MakePatch(from, to);
        var applied = from.Duplicate();
        applied.Tick = to.Tick;
        applied.Merge(patch);
        Assert.Equal(to, applied);
    }

    [Fact]
    public void Merge_AuthShouldOverrideNonAuth()
    {
        var snapshot = Of(0, [(_node, "position", 0), (_other, "position", 0)]);
        var patch = Of(0, [(_node, "position", 1), (_other, "position", 1)], _node);
        Assert.True(snapshot.Merge(patch));
        Assert.Equal(Of(0, [(_node, "position", 1), (_other, "position", 1)], _node), snapshot);
    }

    [Fact]
    public void Merge_AuthShouldUpdateAuth()
    {
        var snapshot = Of(0, [(_node, "position", 0), (_other, "position", 0)], _node);
        var patch = Of(0, [(_node, "position", 1), (_other, "position", 1)], _node);
        Assert.True(snapshot.Merge(patch));
        Assert.Equal(Of(0, [(_node, "position", 1), (_other, "position", 1)], _node), snapshot);
    }

    [Fact]
    public void Merge_NonAuthShouldNotUpdateAuth()
    {
        var snapshot = Of(0, [(_node, "position", 0), (_other, "position", 0)], _node);
        var patch = Of(0, [(_node, "position", 1), (_other, "position", 1)]);
        Assert.True(snapshot.Merge(patch));
        Assert.Equal(Of(0, [(_node, "position", 0), (_other, "position", 1)], _node), snapshot);
    }

    [Fact]
    public void Merge_NonAuthShouldUpdateNonAuth()
    {
        var snapshot = Of(0, [(_node, "position", 0), (_other, "position", 0)]);
        var patch = Of(0, [(_node, "position", 1), (_other, "position", 1)]);
        Assert.True(snapshot.Merge(patch));
        Assert.Equal(Of(0, [(_node, "position", 1), (_other, "position", 1)]), snapshot);
    }

    [Fact]
    public void Erase_ShouldRemoveSubject()
    {
        var snapshot = Of(0, [(_node, "position", 0), (_node, "quaternion", "q"), (_other, "position", 1)]);
        snapshot.EraseSubject(_node);
        Assert.Equal(Of(0, [(_other, "position", 1)]), snapshot);
    }

    [Fact]
    public void Erase_ShouldIgnoreUnknownSubject()
    {
        var snapshot = Of(0, [(_node, "position", 0), (_node, "quaternion", "q")]);
        snapshot.EraseSubject(_other);
        Assert.Equal(Of(0, [(_node, "position", 0), (_node, "quaternion", "q")]), snapshot);
    }

    [Fact]
    public void Sanitize_ShouldDropInvalidSubjectsAndTheirAuth()
    {
        // Regression for snapshot.gd:116, where sanitize() erased nothing
        var snapshot = Of(0, [(_node, "position", 0), (_other, "position", 1)], _node, _other);
        snapshot.Sanitize(s => s == _other);
        Assert.False(snapshot.HasSubject(_node));
        Assert.False(snapshot.IsAuth(_node));
        Assert.True(snapshot.HasSubject(_other, requireAuth: true));
    }
}
