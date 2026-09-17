# netfox-net

Co-op netcode for Godot 4.7 .NET: distributed authority with state synchronization, after Glenn Fiedler's
[Networked Physics in Virtual Reality](https://gafferongames.com/post/networked_physics_in_virtual_reality/). Built for
"friend-slop" games - a handful of friends, shared physics objects, pushing each other, projectiles, joint QTEs - where
nobody cheats and everybody touches everything.

Started as a C# port of [netfox](https://github.com/foxssake/netfox). This branch has left rollback behind; the
`reworked` and `master` branches keep the port.

**[Guides](docs/README.md)** · **[Getting started](docs/getting-started.md)** · **[Design and decisions](docs/design/distributed-authority.md)**

## The model in one paragraph

Every networked object has one peer that simulates it and sends its state - its authority - and everyone else plays
that state back a few ticks behind. Your own character is always yours, so input applies at once with no prediction
and no reconciliation. Touching a crate takes authority over it, and whatever it knocks over follows; grabbing takes
ownership, so nobody can snatch it back. The host arbitrates conflicting claims and gets objects back once they come
to rest. Pushes and hits are events delivered to whoever currently simulates the target.

## Status

Pre-release, under active playtesting. Verified by 82 core tests, 74 Godot-side tests (several stacks in one tree over
a loopback peer) and a three-process ENet mesh smoke under a simulated bad network, all in CI.

Not yet verified: **Steam against a live client** ([#6](https://github.com/matelq/netfox-net/issues/6)). The playground
uses an ENet full mesh that follows the same route.

## Using it

1. Copy `addons/netfox-net` into `res://addons/` (a release zip carries the `Netfox.Core` sources inside it).
2. Enable the plugin in **Project Settings > Plugins**. It registers the `netfox/*` settings and the autoloads.
3. Enable `ImplicitUsings` and `Nullable`, and reference `addons/netfox-net/analyzers/Netfox.SourceGenerators.dll` as
   an `Analyzer` for `[Synced]`.
4. Assign `Multiplayer.MultiplayerPeer`. The clock starts on its own once the session does.
5. Add a `NetworkObject` under each replicated node.

A crate needs no code at all:

```
Crate (RigidBody3D)
├── CollisionShape3D
└── NetworkObject        Kind = Auto → Shared
```

Its transform and velocities are sent; it is frozen wherever another peer simulates it, passes authority to what it
hits, and goes back to the host at rest. A player needs only its own movement:

```csharp
public partial class Player : CharacterBody3D
{
    [Synced] public int Health { get; set; }        // game state; the transform is sent anyway

    public override void _PhysicsProcess(double delta)
    {
        if (!GetNode<NetworkObject>("NetworkObject").Authority.IsLocal) return;
        // read input, MoveAndSlide: crates walked into are taken automatically
    }
}
```

## Layout

| Path | What |
|---|---|
| `addons/netfox-net/` | The addon: `NetworkObject`, the clock, transport helpers, the network simulator. |
| `Netfox.Core/` | Engine-agnostic core: playback clock, sample tracks, clock sync math. |
| `Netfox.SourceGenerators/` | `[Synced]` and its generator. |
| `docs/` | Guides, the design document, a generated API reference. |
| `examples/playground/` | The co-op sample: players, crates, grab and throw, pushes, projectiles, and the smoke check. |
| `examples/steam/` | GodotSteam bootstrap. |
| `test/`, `Netfox.Core.Tests/` | Godot-side tests and xUnit tests. |

## Try the sample

```
sh tools/install-extensions.sh rapier
godot --path . res://examples/playground/playground.tscn
```

Host in one window, Join in the others, or turn on autoconnect - see [Testing on a real network](docs/real-networks.md).
