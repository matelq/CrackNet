using Godot;

namespace Netfox.Extras;

/// <summary>Base for input nodes: Gather() runs before every tick loop, only on the authority. Port of netfox.extras/base-net-input.gd.</summary>
[GlobalClass]
public partial class BaseNetInput : Node
{
    public override void _Ready()
    {
        NetworkTime.Instance.BeforeTickLoop += HandleBeforeTickLoop;
    }

    public override void _ExitTree()
    {
        if (NetworkTime.Instance is not null)
            NetworkTime.Instance.BeforeTickLoop -= HandleBeforeTickLoop;
    }

    private void HandleBeforeTickLoop()
    {
        if (IsMultiplayerAuthority()) Gather();
    }

    /// <summary>Read the local input devices into the input properties.</summary>
    protected virtual void Gather() { }
}
