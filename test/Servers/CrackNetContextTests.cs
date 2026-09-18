using Godot;

namespace CrackNet.Tests;

public partial class CrackNetContextTests : TestSuite
{
    public override Task AfterCase()
    {
        FreeChildren();
        return Task.CompletedTask;
    }

    [Test]
    public void AutoloadsRegisterIntoTheDefaultContext()
    {
        Expect.True(ReferenceEquals(NetworkTime.Instance, CrackNetContext.Default.NetworkTime));
        Expect.True(ReferenceEquals(NetworkEvents.Instance, CrackNetContext.Default.NetworkEvents));
        Expect.True(NetworkTime.Instance.Context.IsDefault);
    }

    [Test]
    public async Task ContextRootGetsItsOwnServers()
    {
        var stack = await Mount(new CrackNetContextRoot { Name = "Stack" });

        Expect.NotNull(stack.Context.NetworkTime);
        Expect.NotNull(stack.Context.NetworkEvents);
        Expect.False(stack.Context.IsDefault);
        Expect.False(ReferenceEquals(stack.Context.NetworkTime, NetworkTime.Instance));

        // The autoloads keep the Instance properties and the default context.
        Expect.True(ReferenceEquals(NetworkTime.Instance, CrackNetContext.Default.NetworkTime));
    }

    [Test]
    public async Task NodesResolveTheContextTheyAreUnder()
    {
        var stack = await Mount(new CrackNetContextRoot { Name = "Stack" });
        var inside = new NetworkObject { Name = "Inside" };
        var insideRoot = new Node { Name = "InsideRoot" };
        insideRoot.AddChild(inside);
        stack.AddChild(insideRoot);

        var outsideRoot = await Mount(new Node { Name = "OutsideRoot" });
        var outside = new NetworkObject { Name = "Outside" };
        outsideRoot.AddChild(outside);

        Expect.True(ReferenceEquals(inside.Context, stack.Context));
        Expect.True(outside.Context.IsDefault);
    }
}
