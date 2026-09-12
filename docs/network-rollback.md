# NetworkRollback and the rollback loop

`NetworkRollback` runs the resimulation. It is an autoload, reached through `NetworkRollback.Instance`.

Most games never touch it: [`RollbackSynchronizer`](rollback-synchronizer.md) implements the whole contract below and
all you write is `RollbackTick`. Read this when you need to know *why* something resimulated the way it did, or when
you are writing a node that takes part in rollback without a synchronizer.

## Why there is a loop at all

Latency means information arrives late, in both directions.

The host receives a client's input for tick 400 when its own clock already says 407. It cannot ignore it - that would
make the client's own movement a lie - so it rewinds to 400, applies the input, and resimulates 400 through 407.

The client receives the host's state for tick 395 when its clock says 402. Its own simulation of 395 may have been
wrong, so it takes the host's version and resimulates 395 through 402 on top of it.

Both sides record what they simulated for each tick, so the next resimulation has something to start from.

Further reading: [Client-Side Prediction and Server Reconciliation][gambetta].

## The loop

It runs inside `NetworkTime`'s `AfterTickLoop`, first, before any handler of yours.

```
BeforeLoop                      nodes say how far back they need to go
  for tick in from..to:
    OnPrepareTick(tick)         restore state and input for the tick
    AfterPrepareTick(tick)      last chance to fill in what is missing, e.g. predicted input
    OnProcessTick(tick)         advance the simulation by exactly one tick
    OnRecordTick(tick + 1)      record the result - note the tick is one further on
AfterLoop                       resimulation done; settle on what to display
```

`OnRecordTick` carrying `tick + 1` is not a quirk to work around: simulating tick T produces the state the world is
in at the *start* of T+1. Every off-by-one in a rollback port comes from this, so it is worth saying twice.

### Where the range comes from

During `BeforeLoop`, anything that needs earlier ticks resimulated calls:

```csharp
NetworkRollback.Instance.NotifyResimulationStart(tick);
```

The earliest tick anyone asks for wins. netfox then clamps the range two ways: never further back than
`HistoryLimit` ticks (with a warning when that bites), and never past `NetworkTime.Tick - 1`.

`RollbackFrom` and `Tick` tell you the range and the tick being resimulated, which is what the status line in the
playground sample prints.

## Conditional simulation

Not every node needs resimulating on every tick of the range - typically because there is no input for it. Nodes that
will be simulated register themselves:

```csharp
NetworkRollback.Instance.NotifySimulated(node);
if (NetworkRollback.Instance.IsSimulated(other)) { /* ... */ }
```

This is for nodes to coordinate with each other; `NetworkRollback` does not use it itself.

## Rollback-awareness

Upstream netfox duck-types this by looking for a `_rollback_tick` method. This port uses interfaces, in
`Rollback/RollbackInterfaces.cs`:

| Interface | Method | When |
|---|---|---|
| `IRollbackTick` | `RollbackTick(double delta, int tick, bool isFresh)` | Advance one tick. |
| `IRollbackSpawnAware` | `RollbackSpawn()` | A rewind reached a tick where this node is alive again. |
| `IRollbackDespawnAware` | `RollbackDespawn()` | A rewind reached a tick before this node existed. |
| `IRollbackDestroyAware` | `RollbackDestroy()` | The node can no longer be respawned by any rewind. Defaults to `QueueFree`. |

`isFresh` is false when this node has already simulated this tick before. Use it for things that must happen once -
playing a sound, spawning an effect - and never for anything that changes rollback state, or the state will differ
between the first pass and the resimulation.

## Mutating another node

If your node changes *another* node's state, tell netfox, or that node will not be resimulated and your change will
be overwritten:

```csharp
target.Health -= 10;
NetworkRollback.Instance.Mutate(target);
```

There is a real trap here. A mutation guarded by `isFresh` happens on the first pass and never again, so a
resimulation that crosses it silently loses it. netfox traces those at `Trace` level - "was not reproduced while
resimulating" - but cannot fix them for you. Write mutations as functions of the tick, so resimulating reproduces
them.

## Knowing when an outcome is settled

Death, sound and visual effects are awkward to take back. Rather than committing on the first pass, you can wait
until the input that decides them can no longer change:

```csharp
var latest = NetworkRollback.Instance.GetLatestInputTick(playerRoot);
if (NetworkRollback.Instance.HasInputForTick(playerRoot, tick)) { /* safe to commit */ }
```

`playerRoot` is whatever the relevant `RollbackSynchronizer` has as its `Root`.

## Settings

Under **Project Settings > Netfox > Rollback**:

- **Enabled** - turns the whole loop off. No events fire.
- **History limit** (64) - how many ticks of history each recorded node keeps. Bigger tolerates more latency and
  costs memory per node. Rollback never goes further back than this.
- **Input redundancy** (3) - how many past ticks of input ride along in each input packet. Input goes out unreliably,
  so this is what covers a dropped packet: the chance of losing a tick's input is `(1 - success) ^ redundancy`. In
  this port the extra copies are encoded as diffs against the newest one, so raising it is cheap for input that is
  not changing.
- **Display offset** (0) - show state this many ticks old. Hides corrections at the cost of showing everything late.
- **Input delay** (0) - shift local input this many ticks into the future, so it arrives on time. Costs
  responsiveness. Note that with input delay larger than the round trip, `isFresh` stops being true for ticks that
  never need resimulating.
- **Enable diff states** (on) - send only the state properties that changed since what that peer last received.

[gambetta]: https://www.gabrielgambetta.com/client-side-prediction-server-reconciliation.html
