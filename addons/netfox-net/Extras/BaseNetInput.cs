using Godot;

namespace Netfox.Extras;

/// <summary>Base for input nodes: Gather() runs before every tick loop, only on the authority. Port of netfox.extras/base-net-input.gd.</summary>
[GlobalClass]
public partial class BaseNetInput : Node
{
    /// <summary>The netfox stack this node uses; resolved when it enters the tree.</summary>
    public NetfoxContext Context { get; private set; } = NetfoxContext.Default;

    public override void _EnterTree()
    {
        Context = NetfoxContext.For(this);
    }

    public override void _Ready()
    {
        Context.NetworkTime.BeforeTickLoop += HandleBeforeTickLoop;
    }

    public override void _ExitTree()
    {
        if (Context.NetworkTime is not null)
            Context.NetworkTime.BeforeTickLoop -= HandleBeforeTickLoop;
    }

    private void HandleBeforeTickLoop()
    {
        if (IsMultiplayerAuthority()) Gather();
    }

    /// <summary>Read the local input devices into the input properties.</summary>
    protected virtual void Gather() { }
}
