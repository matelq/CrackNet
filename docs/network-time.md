# NetworkTime and the tick loop

`NetworkTime` is the clock everything else in netfox hangs off. It is an autoload, reached through
`NetworkTime.Instance`, and it runs a fixed-rate tick loop on top of Godot's variable frame rate.

Ticks are what makes peers comparable. Frames happen whenever a machine gets round to them; tick 412 means the same
moment on every peer, which is the only reason a state recorded for a tick on one machine can be applied to that tick
on another.

## Starting it

```csharp
NetworkTime.Instance.Start();
```

With `NetworkEvents` enabled - it is by default - you do not call this yourself: the session starting and stopping
starts and stops the clock. Calling it as well gets you a warning about doing it twice.

The host's clock is ground truth. A client's `Start` returns immediately but the clock is not usable until it has
synchronized with the host:

```csharp
if (!NetworkTime.Instance.IsInitialSyncDone()) return;   // nothing to simulate yet
```

`AfterSync` fires on the peer once its own initial sync is done. On the host, `AfterClientSync(int peer)` fires as
each client finishes, which is the earliest moment it is worth sending that client anything.

## The loop

One Godot frame can contain no ticks, one tick, or several - whatever the elapsed time calls for, capped by
`MaxTicksPerFrame`. Every frame that runs at least one tick looks like this:

```
BeforeTickLoop                     once, before any tick this frame
  for each tick to simulate:
    BeforeTick(delta, tick)
    OnTick(delta, tick)
    AfterTick(delta, tick)         input for the tick is recorded and sent right after this
  NetworkRollback's loop           see the rollback guide; runs here, first
AfterTickLoop                      once, after the rollback loop
```

Two consequences worth internalising:

- **`BeforeTickLoop` runs once per frame, not once per tick.** `BaseNetInput.Gather` hangs off it, so when a frame
  runs three ticks, all three get the same gathered input. That is deliberate - you cannot poll a keyboard three times
  for one frame's worth of reality - but it means input is not a per-tick function of anything you can control.
- **The rollback loop is already subscribed to `AfterTickLoop`, first.** Your own `AfterTickLoop` handler therefore
  runs after resimulation is finished, which is where to put anything that should see the settled state.

## Reading the clock

| Member | What it is |
|---|---|
| `Tick` | The current tick. During rollback, use `NetworkRollback.Instance.Tick` instead - that is the tick being resimulated. |
| `Time` | Seconds since the clock started, as netfox sees them. |
| `Ticktime` | Seconds per tick, `1.0 / Tickrate`. This is the `delta` your rollback tick gets. |
| `TickFactor` | How far into the current tick the frame is, 0 to 1. What `TickInterpolator` uses. |
| `PhysicsFactor` | Ratio between the tick delta and the delta of whatever frame you are in. Multiply velocities by it around `MoveAndSlide` - see the [caveats](rollback-caveats.md). |
| `RemoteRtt` | Round trip time to the host, in seconds. Zero on the host. |
| `ClockStretchFactor` | How much the local clock is being stretched to converge on the host's. Around 1 when settled. |

`TicksToSeconds`, `SecondsToTicks`, `SecondsBetween` and `TicksBetween` convert between the two, using the negotiated
tickrate rather than the one in your project settings - which matters, because a peer with a different tickrate
setting adopts the host's.

## Do not read the frame clock inside a tick

This is the mistake that costs the most time to find:

```csharp
public void RollbackTick(double delta, int tick, bool isFresh)
{
    // Wrong: a resimulated tick gets a different answer than the first pass did
    var elapsed = Time.GetTicksMsec();
    _cooldown -= GetProcessDeltaTime();
}
```

A tick may be simulated several times, frames apart. Anything read from the wall clock, the frame delta, or a random
number generator that is not rewound will differ between those passes, and the peers stop agreeing. Use the `delta`
and `tick` you are handed, and keep everything else in rollback state.

## Settings

Under **Project Settings > Netfox > Time**:

- **Tickrate** - ticks per second. The host's wins: a client with a different value adopts it and, depending on
  **Tickrate mismatch action**, warns or errors. 30 is a reasonable default; higher costs bandwidth and CPU in
  proportion.
- **Sync to physics** - drives the tick loop from `_PhysicsProcess` at Godot's physics rate instead of from
  `_Process`. Use it when physics bodies take part in rollback.
- **Max ticks per frame** - the ceiling on catching up after a stall, so a hitch does not turn into a freeze.
- **Stall threshold** - a frame longer than this is treated as the game having been paused rather than as time to
  catch up on.
