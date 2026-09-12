# RollbackSynchronizer

The node that turns a subtree into something netfox can rewind. Add it as a child, point `Root` at the node its
property paths are relative to - by default its own parent - and tell it which properties are state and which are
input.

```
Player                          <- Root
  Input                         <- authority: the peer playing this character
  RollbackSynchronizer
```

## State and input

**Input** is what a peer decides: the direction pressed, whether the fire button is down. It is gathered on the peer
that owns the input node and sent to whoever simulates the character.

**State** is what the simulation produces from that input: position, velocity, health. It is recorded per tick,
replicated from the authority, and restored when a rewind lands on a tick.

The split is what makes rollback work. Given the same state at tick T and the same input for T, every peer arrives at
the same state for T+1 - so a late input can be applied by rewinding to T and running forward again.

Two ways to declare them:

```csharp
public partial class PlayerCharacter : CharacterBody3D, IRollbackTick
{
    // Its own property: the attribute is the declaration, and a rename carries the path with it
    [RollbackState] public int JumpsLeft { get; set; }
}

public partial class PlayerInput : BaseNetInput
{
    [RollbackInput] public Vector2 Movement { get; set; }
}
```

Inherited engine properties - `position`, `velocity`, `rotation` - have nowhere to hang an attribute, so those go in
the inspector as strings on the synchronizer's **State properties** array, in `Node:property` form relative to
`Root`:

```
:position          the root itself
:velocity
Input:Movement     a child node
Arm/Hand:rotation  further down
```

Both sources end up in the same arrays. The attributes are read by the editor's gather step and written into the
exported arrays when the scene is saved, which is why the arrays in a saved scene contain more than you typed.

At runtime you can add paths yourself, before or instead of the editor step:

```csharp
synchronizer.AddState(this, "JumpsLeft");
synchronizer.AddInput(inputNode, "Movement");
synchronizer.ProcessSettings();     // re-read the configuration
```

## Simulating

Any node in the managed subtree that implements `IRollbackTick` gets simulated:

```csharp
public void RollbackTick(double delta, int tick, bool isFresh)
{
    Position += Input.Movement * Speed * (float)delta;
}
```

It has to be a function of the state it was given and the input for `tick`, and of nothing else. See the
[rollback guide](network-rollback.md) on `isFresh` and on mutating other nodes, and the
[caveats](rollback-caveats.md) for the ones that bite in practice.

## Authority

Two different authorities matter, and they are usually different nodes:

- The **root's** authority owns the state. It simulates, records and replicates it. In a listen-server game that is
  the host, for every player.
- Each **input node's** authority owns that input. It gathers it and sends it to the peers that simulate with it.

```csharp
player.SetMultiplayerAuthority(1);                       // host owns the state
player.GetNode("Input").SetMultiplayerAuthority(peer);   // the player owns their input
```

Changing authority at runtime means calling `ProcessAuthority()`, or `ProcessSettings()` if anything else changed
too. netfox re-runs this by itself when a session starts.

## Prediction

With **Enable prediction** on, a node is simulated even when its input has not arrived, by reusing the last input
known for it. That keeps other players moving instead of freezing, at the cost of being wrong sometimes - which the
next state from the authority corrects.

```csharp
if (!Synchronizer.HasInput()) { /* no input at all yet */ }

var age = Synchronizer.GetInputAge();      // ticks since the newest known input, -1 if none
if (Synchronizer.IsPredicting())
{
    // This tick is running on a guess. Do not commit anything irreversible here.
}
```

`IsPredicting` answers about the node currently being simulated when called from inside a rollback tick, and about
the synchronizer's own input otherwise.

Predicted state is still recorded, which is usually what you want. When it is not - a guess so poor it is worse than
holding still - keep it out of history:

```csharp
Synchronizer.IgnorePrediction(node);
```

## Spawning and despawning

A node that appears mid-game needs a spawn tick, or a rewind to before it existed will simulate it anyway:

```csharp
synchronizer.Spawn();          // spawned now
synchronizer.Spawn(tick);      // spawned at a specific tick
synchronizer.Despawn();
if (synchronizer.IsAlive()) { /* ... */ }
```

Entering the tree sets `SpawnTick` to the next tick by itself, so a node added during play is already handled; call
`Spawn` explicitly when you know a different tick, for example the tick the input that created it belongs to.

Nodes implementing `IRollbackSpawnAware`, `IRollbackDespawnAware` or `IRollbackDestroyAware` are called as rewinds
cross those ticks. Once a node can no longer be respawned by any rewind, `RollbackDestroy` runs - `QueueFree` by
default.

## Related nodes

- **`TickInterpolator`** - smooths named properties between ticks for display. Rollback runs at the tickrate; your
  monitor does not.
- **`PredictiveSynchronizer`** - the same machinery without any networking, for short-lived or deterministic objects
  that every peer can simulate identically.
- **`StateSynchronizer`** - replicates state without rollback, for things that do not need rewinding.
- **`PeerVisibilityFilter`** - see [visibility filters](visibility-filters.md). One is added as a child
  automatically.
