using Godot;
using Netfox.Extras;

namespace Netfox.Examples.Snippets;

/// <summary>
/// Input that stays true for exactly one tick, no matter how many frames the button was held or how many frames fit in
/// a tick. The press is buffered as it happens and handed over once per tick.
/// Port of examples/snippets/input-gathering-tutorial/one-off-input.gd.
/// </summary>
public partial class OneOffInput : BaseNetInput
{
    /// <summary>The action to watch; the snippet upstream hardcodes "move_jump".</summary>
    [Export] public string Action { get; set; } = "move_jump";

    /// <summary>True during the single tick that follows the press.</summary>
    public bool IsJumping { get; private set; }

    private bool _isJumpingBuffer;
    private Action<double, int>? _afterTickHandler;

    public override void _Ready()
    {
        base._Ready();

        _afterTickHandler = (_, _) => GatherAlways();
        Context.NetworkTime.AfterTick += _afterTickHandler;
    }

    public override void _ExitTree()
    {
        base._ExitTree();

        // C# events outlive the node unless they are disconnected here, unlike Godot signals
        if (_afterTickHandler is not null && Context.NetworkTime is { } time)
            time.AfterTick -= _afterTickHandler;
        _afterTickHandler = null;
    }

    public override void _Process(double delta)
    {
        if (Input.IsActionJustPressed(Action)) _isJumpingBuffer = true;
    }

    private void GatherAlways()
    {
        IsJumping = _isJumpingBuffer;
        _isJumpingBuffer = false;
    }
}
