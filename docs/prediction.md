# Prediction

Input arrives late. The host only ever knows a client's input from a few ticks ago; other clients are further behind
still, because their copy has to go to the host and back. Prediction is what keeps everything moving in the meantime.

## What netfox does on its own

By default a node is simulated only for ticks it has input for. No input, no simulation - the character just stops,
and jerks forward when the input finally lands.

Turn **Enable prediction** on for a `RollbackSynchronizer` and it is simulated anyway, reusing the newest input known
for it. For most games that alone is enough: a player who was running left a moment ago is probably still running
left, and the next state from the authority corrects the guess.

```csharp
synchronizer.EnablePrediction = true;
```

The cost is being wrong sometimes. The corrections arrive as state from the authority, and the visible result is the
character snapping. That is what the display offset and the interpolator are for.

## Predicting better

"Reuse the last input" is a default, not a law. A racing game can assume full throttle continues; a shooter probably
should not assume the trigger is still held. To do better, fill the input in yourself during
`NetworkRollback.AfterPrepareTick` - the point where state and input for the tick have been restored and the tick has
not been simulated yet:

```csharp
public partial class PlayerInput : BaseNetInput
{
    [RollbackInput] public Vector3 Movement { get; set; }

    /// <summary>How much to trust this tick's input, for whatever the character wants to do with it.</summary>
    public float Confidence { get; private set; } = 1.0f;

    private RollbackSynchronizer _synchronizer = null!;

    public override void _Ready()
    {
        base._Ready();
        _synchronizer = GetNode<RollbackSynchronizer>("../RollbackSynchronizer");
        NetworkRollback.Instance.AfterPrepareTick += Predict;
    }

    public override void _ExitTree()
    {
        base._ExitTree();
        if (NetworkRollback.Instance is { } rollback) rollback.AfterPrepareTick -= Predict;
    }

    protected override void Gather()
        => Movement = new Vector3(Godot.Input.GetAxis("move_west", "move_east"), 0,
                                  Godot.Input.GetAxis("move_north", "move_south"));

    private void Predict(int tick)
    {
        if (!_synchronizer.IsPredicting())
        {
            // Real input for this tick; nothing to guess
            Confidence = 1.0f;
            return;
        }

        if (!_synchronizer.HasInput())
        {
            // Nothing to guess from either - this peer has never sent us anything
            Confidence = 0.0f;
            Movement = Vector3.Zero;
            return;
        }

        // The older the input, the less it is worth trusting. Fading it out beats carrying a stale direction forward.
        var age = _synchronizer.GetInputAge();
        Confidence = Mathf.Clamp(1.0f - age / 12.0f, 0.0f, 1.0f);
        Movement *= Confidence;
    }
}
```

Remember to unsubscribe in `_ExitTree`: C# events are not disconnected when a node is freed, unlike Godot signals.

## The three questions

| Call | Answers |
|---|---|
| `IsPredicting()` | Is this tick running on a guess? Inside a rollback tick it answers about the node being simulated; outside, about this synchronizer's own input. |
| `HasInput()` | Is there any known input at all to base a guess on? |
| `GetInputAge()` | How many ticks old the newest known input is. `0` means it is for this very tick; `-1` means there is none. |

## When a guess is worse than nothing

Predicted state is recorded like any other, which is usually right - it gives the next tick something to build on.
When a guess is bad enough that you would rather have no history than wrong history, keep it out:

```csharp
synchronizer.IgnorePrediction(node);
```

The tick still runs; its result just is not recorded.

## Do not commit to anything irreversible

A predicted tick can be replaced by a corrected one a few frames later. Deaths, explosions, score changes and sounds
should not fire from one.

```csharp
public void RollbackTick(double delta, int tick, bool isFresh)
{
    Simulate(delta);

    // isFresh alone is not enough: a fresh tick can still be running on predicted input
    if (isFresh && !Synchronizer.IsPredicting() && Health <= 0)
        Die();
}
```

For outcomes you want to hold until they are certain rather than merely unpredicted, ask whether the input that
decides them has arrived:

```csharp
if (NetworkRollback.Instance.HasInputForTick(playerRoot, tick)) { /* settled */ }
```

See also [`RewindableAction`](rollback-synchronizer.md#related-nodes) for actions that are confirmed or cancelled as
the ground truth arrives.
