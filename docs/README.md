# netfox-net guides

A native C# port of [netfox](https://github.com/foxssake/netfox): rollback netcode for Godot, with client-side
prediction and server reconciliation. Same semantics and the same public API as the original, written as idiomatic
C# rather than bridged to GDScript.

These guides cover the parts you need to build with it. They assume you know Godot's high-level multiplayer basics -
peers, authority, RPCs - and not much else.

## Start here

0. **[Getting started](getting-started.md)** - a rollback-networked character from nothing, ending where
   `examples/playground` begins.
1. **[NetworkTime and the tick loop](network-time.md)** - the fixed-rate clock everything hangs off, what runs in
   what order, and why reading the frame clock inside a tick breaks things.
2. **[RollbackSynchronizer](rollback-synchronizer.md)** - the node that makes a subtree rewindable. State versus
   input, authority, spawning and despawning.
3. **[Rollback caveats](rollback-caveats.md)** - the Godot APIs that do not expect to be rewound, and what to do
   about each. Read this before debugging anything.
4. **[Rollback on a real network](real-networks.md)** - what changes once two people play over a real connection:
   where authority actually lives, the conditions on prediction, why a two-player LAN test proves nothing about
   contact, and how to test under loss and jitter. Read this before your first playtest.

Then, as you need them:

- **[NetworkRollback and the rollback loop](network-rollback.md)** - the contract underneath the synchronizer: how
  the resimulation range is chosen, what `isFresh` means, how to mutate another node safely.
- **[Prediction](prediction.md)** - keeping other players moving when their input has not arrived, and not
  committing to anything you cannot take back.
- **[Network schemas](network-schemas.md)** - telling netfox how to encode a property, once bandwidth matters.
- **[Visibility filters](visibility-filters.md)** - deciding per peer what gets replicated at all.
- **[API reference](api.md)** - every public type and member with its summary, generated from the XML doc comments.

## Learning by running something

`examples/playground` is a small complete game built out of scenes: host or join by address, a few players, movement
with rollback and prediction, and a platform to ride. Its [README](../examples/playground/README.md) points at the
parts worth reading, and [TESTING.md](../examples/playground/TESTING.md) walks through running and breaking it from
scratch - the fastest way to see what rollback actually does.

```
<godot> --path . res://examples/playground/playground.tscn
```

## Differences from the GDScript original

The semantics are the same - this port is checked tick by tick against upstream, see `parity/`. What differs is how
you write against it:

| Original | Here |
|---|---|
| `_rollback_tick(delta, tick, is_fresh)` | `IRollbackTick.RollbackTick(double, int, bool)` |
| `_rollback_spawn` / `_rollback_despawn` / `_rollback_destroy` | `IRollbackSpawnAware` / `IRollbackDespawnAware` / `IRollbackDestroyAware` |
| `_get_rollback_state_properties()` and friends | `[RollbackState]`, `[RollbackInput]`, `[SynchronizedState]`, `[Interpolated]` attributes, or the interfaces by hand |
| Signals (`NetworkTime.before_tick.connect`) | C# events (`NetworkTime.Instance.BeforeTick += …`) |
| Autoloads by name | `NetworkTime.Instance` and the other `Instance` properties |
| `snake_case` | `PascalCase` |

Two things that are easy to get wrong coming from GDScript:

- **C# events are not disconnected when a node is freed.** Godot does that for signals; it cannot for events. Store
  the delegate and unsubscribe in `_ExitTree`, or you leak and get called on a dead node.
- **`Variant` has no value equality.** Use `VariantComparer` or `Snapshot.ValueComparer` when comparing replicated
  values.

The full table, the three things that catch GDScript users out, and every deliberate deviation with its reasoning
are in the **[migration notes](migration.md)**.
