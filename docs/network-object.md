# NetworkObject

One `NetworkObject` per replicated thing, as a child of the node it replicates (its root). It holds who simulates the
object and who holds it, sends the root's state while this peer is the authority, and plays that state back
everywhere else. The reasoning behind every rule here is in the [design document](design/distributed-authority.md).

## Kinds

`Kind` decides how authority moves. The default, `Auto`, reads the root's type.

| Kind | Rule | Auto for | Examples |
|---|---|---|---|
| `Personal` | stays with one peer, passes authority on contact | `CharacterBody2D/3D`, plain `Node2D/3D` | a player, a projectile |
| `Shared` | taken by touch or grab, passes authority on contact | `RigidBody2D/3D`, `VehicleBody3D` | a crate, a ball |
| `World` | stays put, does not pass authority on contact | `StaticBody`, `AnimatableBody`, `Area`, `Node`, `Control` | a lift, a door, the score |
| `Custom` | `Transferable` and `SpreadsAuthority` by hand, no physics handling | never | anything unusual |

Pick a kind by hand only when the type says the wrong thing: a grenade is a rigid body nobody may take, so `Personal`.

## Authority and ownership

| Member | Meaning |
|---|---|
| `Authority.Peer`, `Authority.IsLocal` | The peer that simulates the object and sends its state (Godot's multiplayer authority of the root), and whether it is this one. |
| `Holder` | The peer holding the object, or 0. A held object cannot be taken by anyone else. |
| `AuthorityChanged` | Raised on every peer after authority or holder changed. |

Every change is optimistic: it applies on the requesting peer at once and goes to the host, which accepts it or
corrects the requester. A request returns false when it cannot even be tried (someone else holds the object, it is
not transferable, or this peer is not connected); true means applied here and sent, not yet accepted.

For physics roots the library makes these calls itself: a rigid body touches what it hits while moving and returns to
the host at rest, a character body touches what it slides into. Games call:

| Call | When |
|---|---|
| `TryClaim()` | The object becomes this peer's: authority and ownership, nobody else can take it. The body is frozen while claimed; move it by hand. |
| `Throw(velocity)` | Lets go with a velocity: the throw flies on this peer's simulation. |
| `Release()` | Lets go without one. |
| `Touch(other)` | Contact the physics engine does not report: a projectile that moves itself, a melee swing. |
| `Authority.Take()`, `Authority.ReturnToHost()` | What physics bodies do themselves, by hand: for `Custom` objects. |

Conflicts are settled by the host. A grab beats a touch, and of two touches the first one to arrive wins.
`MaxSpreadDepth` limits how many objects a chain can pass authority through, counted from its source.

The rule to hold on to: **every interaction has exactly one arbiter**, the authority of the object that started it.
If two peers can both decide the same hit or grab, the mechanic is not finished.

## Pushes and events

Every call below also works on the game's own node (`crate.Push(...)`); `node.Net()` returns its `NetworkObject`.

```csharp
this.Push(target, impulse);                              // my object struck yours: takes a crate, pushes a player
target.Push(impulse);                                    // nothing doing the pushing: an explosion, a trap
Velocity += this.Net().TakeKnockback(delta);             // a character applies the pushes it received
```

A push reaches whoever simulates the object: a rigid body takes the impulse itself; anything else adds it to
`TakeKnockback` and raises `Pushed`. `Push(target, impulse)` first takes the target when it can, so a crate flies on
the striker's simulation at once; if the host gives the crate to someone else instead, that push is lost with the
claim. Players do not collide with each other, since each would push a copy of the other in the past: a push is how
they shove. **Push Strength** on a character's `NetworkObject` pushes the rigid bodies it walks into.

```csharp
target.Object.Send("opened");                            // from anyone
Object.Received += (fromPeer, payload) => { ... };       // on the authority
```

Both are delivered reliably, exactly once, to the authority. If authority moves while one is on its way, the peer that
no longer simulates the object passes it on. On the authority itself it is raised at once - unless that authority is
a claim the host has not confirmed yet: then it waits for the host's answer, and goes to the winner if the claim lost.

## Projectiles and hitscan

A projectile belongs to its shooter: spawn it with its generated `Spawn`, and its plain `Node3D` root makes it
`Personal`. The shooter's peer moves it and decides every hit against the targets it displays: it pushes the target
and calls `Despawn()` in the same frame, so a projectile cannot pass through its first target or hit twice. To push a
crate with one, `this.Push(crate, impulse)` takes it and pushes it. `PlaygroundShot` is the worked example.

Hitscan needs nothing extra: the shooter runs an ordinary ray query. Bodies other peers simulate sit frozen at their
displayed positions, so the ray hits what the shooter sees.

## Spawning, despawn and teleport

- `Shot.Spawn(node, data)` and `Shot.Spawn(at: transform, data)`, generated for `[Scene]` and `ISpawnedWith<T>` classes,
  instance the class's scene on every peer and on late joiners; `OnSpawned(data)` runs everywhere before it enters the
  tree. See [Getting started](getting-started.md#spawning).
- `Despawn()` ends the object's timeline. The authority hides it and stops processing at once. Other peers keep
  showing it until their playback reaches the final sample, then hide it; the root is freed everywhere after a grace
  period. Do not `QueueFree` a replicated object yourself.
- `Teleport()` makes the next sample apply without blending: a respawn, not a flight across the map.

## What is sent

`SyncedSummary` in the inspector lists it, in order:

- the root's full transform, for any 2D or 3D root;
- velocity for a character body, linear and angular velocity for a rigid body;
- every `[Synced]` property of the root and its descendants, down to a nested `NetworkObject`.

Nothing else. State goes out every `NetworkObjectServer.StateIntervalTicks` ticks (15 Hz at the default 30 Hz tick);
an unchanged object only once a second. Every object that changed is packed into as few packets per peer as fit
**Max Sync Packet Size** (1200 bytes by default, under the MTU of any real route).

**One object has to fit in one packet.** A crate is about 80 bytes. An object whose state is larger - a long array, a
dictionary, dozens of properties - is sent as a packet over the limit, which the transport fragments, and losing any
fragment loses the whole sample; a warning says which object. Split it into several `NetworkObject`s, or send large,
rarely changing state as an event.

## Playback

A remote object is shown from its authority's samples, a little in the past:

- **One clock per remote peer.** Everything one peer sends is shown at the same tick, so a player and the crate it
  carries never drift apart.
- **An adaptive buffer per link.** Depth is the send interval plus that link's measured jitter, capped at 20 ticks.
  The buffer absorbs jitter; it cannot absorb latency, because nothing can be shown before it arrives.
- **No freezing, no rewriting.** On underrun the object holds its last value; after an outage playback catches up
  quickly. A late sample never rewrites what was already shown.
- **Objects start at their first sample.** A spawned object is hidden until playback reaches its first sample. A
  projectile starts at the muzzle, not hanging there or appearing down range.

`NetworkObjectServer.Instance.Diagnostics.GetPlaybackStatus(peer)` reports, averaged over a second, how old that peer's
state is on arrival and how long it waits in the buffer. `Object.Diagnostics` has the sequences, the display tick and
the sample events, for checks rather than game logic.
