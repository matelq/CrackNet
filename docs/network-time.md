# NetworkTime and the tick loop

`NetworkTime` is the clock everything else in CrackNet hangs off. It is an autoload, reached through
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
`MaxTicksPerFrame`:

```
BeforeTickLoop                     once, before any tick this frame
  for each tick to simulate:
    BeforeTick(delta, tick)
    OnTick(delta, tick)
    AfterTick(delta, tick)         NetworkObject state is sent from here, every StateIntervalTicks
AfterTickLoop                      once, after the ticks
```

Playback of remote objects runs in `_Process`, every frame, so displayed motion is smooth between ticks.

## Reading the clock

| Member | What it is |
|---|---|
| `Tick` | The current tick. Samples are stamped with it. |
| `Time` | Seconds since the clock started, as CrackNet sees them. |
| `Ticktime` | Seconds per tick, `1.0 / Tickrate`. |
| `TickFactor` | How far into the current tick the frame is, 0 to 1. |
| `PhysicsFactor` | Ratio between the tick delta and the delta of whatever frame you are in. |
| `RemoteRtt` | Round trip time to the host, in seconds. Zero on the host. |
| `ClockStretchFactor` | How much the local clock is being stretched to converge on the host's. Around 1 when settled. |

`TicksToSeconds`, `SecondsToTicks`, `SecondsBetween` and `TicksBetween` convert between the two, using the negotiated
tickrate rather than your own - which matters, because a peer whose physics rate differs adopts the host's.

## Settings

The tick is the physics step, so the tickrate is `physics/common/physics_ticks_per_second` - Godot's own setting, 60 by
default. CrackNet adds one number next to it, under **Project Settings > CrackNet > Time**:

- **State interval ticks** - physics steps between two snapshots. 2 at 60 Hz physics sends state 30 times a second.
  Every peer must agree on it, and on the physics rate: the tickrate handshake warns when a peer reports another one.

Everything else about the clock - the ceiling on catching up after a hitch, what counts as a pause rather than time to
catch up on, how often the clock is measured against the host's and how gently the error is taken - is a number in
`CrackNetSettings` with no project setting behind it. Each says in its own documentation what goes wrong if it is
changed. A game that has to change one assigns a `CrackNetSettings` to `CrackNetSettings.Instance` from an autoload
ordered above CrackNet's, before the autoloads enter the tree.
