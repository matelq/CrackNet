# Rollback caveats

Rollback means a tick can be simulated several times, in the same frame, out of order with the wall clock. Most Godot
APIs were not written with that in mind. These are the ones that bite.

## MoveAndSlide runs at the wrong speed

`MoveAndSlide` multiplies `Velocity` by the delta of whatever frame it is called from. Inside a rollback tick that is
not the tick delta, so the character moves at the wrong speed - and differently depending on frame rate.

`NetworkTime.PhysicsFactor` is the ratio between the two, correct in both process and physics frames. Scale around
the call, and scale back if the velocity has to survive into the next tick:

```csharp
var factor = (float)NetworkTime.Instance.PhysicsFactor;
Velocity = velocity * factor;
MoveAndSlide();
Velocity /= factor;
```

## IsOnFloor is stale after a rewind

`CharacterBody2D`/`3D` only update `IsOnFloor` during `MoveAndSlide`. A rewind restores the position but not the
flag, so the first read in a resimulated tick is whatever the previous pass left behind - and gravity or jumps go
wrong for that tick.

A zero length move refreshes it:

```csharp
private void RefreshIsOnFloor()
{
    var velocity = Velocity;
    Velocity = Vector3.Zero;
    MoveAndSlide();
    Velocity = velocity;
}
```

Call it before the first `IsOnFloor` of the tick. `examples/playground` does exactly this.

## Physics does not step with ticks

Godot's physics server steps in `_PhysicsProcess`. Rollback advances the game several times inside one frame, and
nothing steps physics along with it. So:

- **Kinematic bodies work.** `CharacterBody` moves through `MoveAndSlide`, which does its own collision queries.
- **RigidBodies do not**, out of the box. Their state lives inside the physics engine, which is neither stepped nor
  rewound by netfox.
- **Physics queries can be stale.** `IntersectShape`, `ShapeCast` and friends see the transforms the last physics
  step left behind, not what rollback just restored.

For a query to see a rolled-back body, push its transform into the server by hand:

```csharp
// Works for both Godot Physics and Jolt
private void ForceUpdatePhysicsTransform()
{
    var rid = GetRid();
    PhysicsServer3D.BodySetMode(rid, PhysicsServer3D.BodyMode.Static);
    PhysicsServer3D.BodySetState(rid, PhysicsServer3D.BodyState.Transform, GlobalTransform);
    PhysicsServer3D.BodySetMode(rid, PhysicsServer3D.BodyMode.Kinematic);
}
```

Nodes with a `ForceUpdate`-style method of their own - `ShapeCast3D.ForceShapecastUpdate`,
`PhysicsBody3D.MoveAndCollide` in test-only mode - can use those instead.

For real RigidBody rollback, `Netfox.Extras` has physics drivers that step and snapshot the whole space:
`RapierPhysicsDriver2D/3D` drive the Rapier extension, and `GodotPhysicsDriver2D/3D` need a Godot build carrying
[PR 76462](https://github.com/godotengine/godot/pull/76462), which is not in a release yet. The Rapier path is
verified - see `examples/physics/RapierCheck.tscn`.

## Anything that is not state, is not rewound

The rule behind most surprises: if a value affects the simulation and is not in the synchronizer's state properties,
a rewind does not restore it, and the resimulated tick computes something different from the first pass.

That covers more than it sounds like:

- **Edge detection.** "Pressed this tick but not last" needs last tick's input in state, or it fires again on every
  resimulation.
- **Cooldowns and timers.** Decrement them by the tick delta and keep the remainder in state. A `Godot.Timer` runs on
  the frame clock and is not rewound.
- **Random numbers.** A generator whose seed is not rewound gives different answers each pass. `Netfox.Extras` has
  `RewindableRandomNumberGenerator` for this.
- **The wall clock.** `Time.GetTicksMsec`, `GetProcessDeltaTime` and the like differ between passes by construction.

## State machines

A state machine usually assumes it is stepped forward once per frame and never snapped backwards. Watch for:

- cooldowns guarding transitions,
- conditions reading values that are not rollback state,
- transitions that require a particular order,
- side effects that fire on any state change.

netfox records the configured state per tick; a rewind snaps everything back and replays forward inside one frame.
`Netfox.Extras` has `RewindableStateMachine`, which is built for that.

## isFresh is not a guarantee of correctness

`isFresh` tells you this node has not simulated this tick before. It does *not* tell you the input was real - with
prediction on, a fresh tick can be running on a guess - and with input delay larger than the round trip it can stop
being true for ticks that never need resimulating.

Use it for once-only side effects, check `IsPredicting()` before anything irreversible, and never let it decide
something that changes rollback state: the first pass would then produce a different result than the resimulation,
which is exactly what rollback cannot tolerate.
