using Godot;

namespace CrackNet.Tests;

/// <summary>
/// Raises <see cref="Drawn"/> at the end of every rendered frame, after every node processed and every deferred call
/// ran: what a player sees. A check that reads positions right after <c>await NextFrame()</c> reads them at the start
/// of the frame, before playback, animation and the library's placement of attached items have moved anything.
/// </summary>
public partial class DrawnFrame : Node
{
    public event Action? Drawn;

    public DrawnFrame() => ProcessPriority = int.MaxValue;

    public override void _Process(double delta) => Callable.From(Raise).CallDeferred();

    // A call deferred in the frame this node was freed in still runs: by then what the handler reads is gone
    private void Raise()
    {
        if (IsInsideTree() && !IsQueuedForDeletion()) Drawn?.Invoke();
    }

    public override void _ExitTree() => Drawn = null;
}
