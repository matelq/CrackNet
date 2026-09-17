# netfox-net guides

Co-op netcode for Godot C#: every object is simulated by one peer - its authority - and played back everywhere else.
Authority moves to whoever interacts with an object, and the host settles conflicts. The guides assume you know
Godot's high-level multiplayer basics: peers, multiplayer authority, `MultiplayerSpawner`.

## Start here

0. **[Getting started](getting-started.md)** - install, connect, and replicate a player and a crate.
   **[Minimal examples](examples.md)** - players, physics objects, players with objects, players with each other,
   shooting: the smallest code for each.
1. **[NetworkObject](network-object.md)** - kinds, authority and ownership, grabbing and throwing, knocks and events,
   projectiles, spawning, what is sent, and how playback shows remote objects.
2. **[NetworkTime](network-time.md)** - the shared tick clock that stamps every sample.
3. **[Testing on a real network](real-networks.md)** - simulated network profiles, autoconnect with several editor
   instances, the delay readout, and the smoke check.

Then:

- **[Design](design/distributed-authority.md)** - the model, why not rollback or snapshot interpolation, every decision
  and what is deferred. Read it before changing anything networked in the library.
- **[API reference](api.md)** - every public type and member, generated from the XML doc comments.

## The sample

`examples/playground` is a small co-op scene: players, a stack of crates, grab and throw, pushes, slow projectiles, late
join, and a full ENet mesh with a simulated bad network. Its code is the reference for the patterns in these guides.

```
<godot> --path . res://examples/playground/playground.tscn
```

## Two things that catch C# users out

- **C# events are not disconnected when a node is freed.** Godot does that for signals; it cannot for events. Store
  the delegate and unsubscribe in `_ExitTree`.
- **`Variant` has no value equality.** Use `VariantComparer` when comparing replicated values.
