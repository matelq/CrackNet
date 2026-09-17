using Godot;

namespace Netfox.Tests;

public partial class NetfoxContextTests : TestSuite
{
    public override Task AfterCase()
    {
        FreeChildren();
        return Task.CompletedTask;
    }

    [Test]
    public void AutoloadsRegisterIntoTheDefaultContext()
    {
        Expect.True(ReferenceEquals(NetworkTime.Instance, NetfoxContext.Default.NetworkTime));
        Expect.True(ReferenceEquals(NetworkEvents.Instance, NetfoxContext.Default.NetworkEvents));
        Expect.True(NetworkTime.Instance.Context.IsDefault);
    }

    [Test]
    public async Task ContextRootGetsItsOwnServers()
    {
        var stack = await Mount(new NetfoxContextRoot { Name = "Stack" });

        Expect.NotNull(stack.Context.NetworkTime);
        Expect.NotNull(stack.Context.NetworkEvents);
        Expect.False(stack.Context.IsDefault);
        Expect.False(ReferenceEquals(stack.Context.NetworkTime, NetworkTime.Instance));

        // The autoloads keep the Instance properties and the default context.
        Expect.True(ReferenceEquals(NetworkTime.Instance, NetfoxContext.Default.NetworkTime));
    }

    [Test]
    public async Task NodesResolveTheContextTheyAreUnder()
    {
        var stack = await Mount(new NetfoxContextRoot { Name = "Stack" });
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
