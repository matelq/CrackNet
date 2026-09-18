# NetworkObject

One `NetworkObject` per replicated thing, as a child of the node it replicates (its root). It holds who simulates the
object and who holds it, sends the root's state while this peer is the authority, and plays that state back
everywhere else. The reasoning behind every rule here is in the [design document](design/distributed-authority.md).

## Kinds

`Kind` decides how authority moves. The default, `Auto`, reads the root's type.

| Kind | Rule | Auto for | Examples |
|---|---|---|---|
| `Personal` | stays with one peer, passes authority on contact | `CharacterBody3D`, plain `Node3D` | a player, a projectile |
| `Shared` | taken by touch or grab, passes authority on contact | `RigidBody3D`, `VehicleBody3D` | a crate, a ball |
| `World` | stays put, does not pass authority on contact | `StaticBody`, `AnimatableBody`, `Area`, `Node`, `Control` | a lift, a door, the score |
| `Custom` | `Transferable` and `SpreadsAuthority` by hand, no physics handling | never | anything unusual |

Pick a kind by hand only when the type says the wrong thing: a grenade is a rigid body nobody may take, so `Personal`.

## Authority and ownership

| Member | Meaning |
|---|---|
| `Authority.Peer`, `Authority.IsLocal` | The peer that simulates the object and sends its state (Godot's multiplayer authority of the root), and whether it is this one. |
| `ClaimedBy` | The peer holding the object, or 0. A held object cannot be taken by anyone else. |
| `PlaybackState` | `Pending` until playback here reaches the first sample, `Playing`, then `Ending` after a despawn. What a projectile checks before it hits someone, instead of `Visible`. |
| `AuthorityChanged` | Raised on every peer after authority or holder changed; for watching someone else's object. |

Every change is optimistic: it applies on the requesting peer at once and goes to the host, which accepts it or
corrects the requester. A request returns false when it cannot even be tried (someone else holds the object, it is
not transferable, or this peer is not connected); true means applied here and sent, not yet accepted.

For physics roots the library makes these calls itself: a rigid body touches what it hits while moving and returns to
the host at rest, a character body touches what it slides into. Games call:

| Call | When |
|---|---|
| `TryClaim()` | The object becomes this peer's: authority and ownership, nobody else can take it. The body is frozen while claimed; move it by hand. |
| `ReleaseClaim(velocity)` | Lets go with a velocity: the throw flies on this peer's simulation. |
| `ReleaseClaim()` | Lets go without one. |
| `Spread(other)` | Contact the physics engine does not report: a projectile that moves itself, a melee swing. |
| `Authority.Take()`, `Authority.ReturnToHost()` | What physics bodies do themselves, by hand: for `Custom` objects. |

Conflicts are settled by the host. A grab beats a touch, and of two touches the first one to arrive wins.
`MaxSpreadDepth` limits how many objects a chain can pass authority through, counted from its source.

The rule to hold on to: **every interaction has exactly one arbiter**, the authority of the object that started it.
If two peers can both decide the same hit or grab, the mechanic is not finished.

## Events and state

Every call below also works on the game's own node: `crate.Impulse(...)`, `crate.TryClaim()`, `this.Authority.IsLocal`,
`this.ImpulseVelocity`, `crate.ClaimedBy`. `node.Net()` returns the `NetworkObject` itself, for the rarer
`Send` and `Diagnostics`.

Every one of them needs a `NetworkObject` on the node it reaches, and the build says so when it is missing: `CRN006`
asks the class you used it on for a scene whose root has a `NetworkObject` as a direct child. Calls on engine types
(`PhysicsBody3D` from a shape query) are left alone, since nothing at build time says what those nodes will be. A class
built in code instead of instantiated from a scene — a test, a generated level — silences the rule with
`#pragma warning disable CRN006`.

**How a node learns of events.** One rule: the node itself implements an interface named after the event, with an
`On...` method, and there is nothing to unsubscribe in `_ExitTree`. The C# event of the same name on `NetworkObject`
is for watching someone else's object. `CRN006` also fires for a class implementing one of these without a
`NetworkObject` in its scene, because the method would compile and never be called.

| On the node | Called | Watching another object |
|---|---|---|
| `IAuthorityChanged.OnAuthorityChanged()` | on every peer, after authority or holder changed | `Net().AuthorityChanged` |
| `IImpulsed.OnImpulsed(impulse)` | on the authority, once per push, for a root that is not a rigid body | `Net().Impulsed` |
| `ISpawnedWith<T>.OnSpawned(args)` | on every peer, before the root enters the tree | |

```csharp
public partial class Crate : RigidBody3D, IAuthorityChanged
{
    public void OnAuthorityChanged() => Tint(this.Authority.Peer);   // every peer, after the change applied
}
```

**Read state, do not remember results.** `TryClaim()` is optimistic: it returns true the moment the claim applied
here, and the host is asked afterwards. When two players grab one crate within a ping, both see it in hand, and a
round trip later the host's answer takes it out of one player's hands: `ClaimedBy` changes and `OnAuthorityChanged`
runs on the loser. Logic that keyed off the return value (`_carrying = true`, the carrying animation on) is now wrong
on that peer; logic that reads `ClaimedBy` in `OnAuthorityChanged` corrects itself in the same frame. The same holds
for `Authority.IsLocal` and `PlaybackState`: they are always right on this peer, a call's result was right when it
returned.

```csharp
this.Impulse(target, impulse);                              // my object struck yours: takes a crate, pushes a player
target.Impulse(impulse);                                    // nothing doing the pushing: an explosion, a trap
Velocity += this.ImpulseVelocity;                           // a character applies the pushes it received
```

A push reaches whoever simulates the object: a rigid body takes the impulse itself; anything else adds it to
`ImpulseVelocity` and calls `OnImpulsed`. `ImpulseVelocity` is the velocity the pushes gave, fading each physics frame
by **Impulse Decay** on the `NetworkObject`; a character adds it where it composes its `Velocity`, next to gravity,
every frame. It has to be there and not applied by the library: a controller writes its horizontal velocity from input
each frame, so a push added once would last one frame, and only the controller can give an upward push its arc.
`Impulse(target, impulse)` first takes the target when it can, so a crate flies on the striker's simulation at once; if
the host gives the crate to someone else instead, the push is passed on to the winner. It arrives a round trip late, so
two players striking one crate from opposite sides send it one way and then the other rather than cancelling out.
Players do not collide with each other, since each would push a copy of the other in the past: a push is how they
shove. **Impulse Strength** on a character's `NetworkObject` pushes the rigid bodies it walks into.

```csharp
target.Object.Send("opened");                            // from anyone
Object.Received += (fromPeer, payload) => { ... };       // on the authority
```

Both are delivered reliably, exactly once, to the authority. If authority moves while one is on its way, the peer that
no longer simulates the object passes it on. On the authority itself it is raised at once - unless that authority is
a claim the host has not confirmed yet: then it waits for the host's answer, and goes to the winner if the claim lost.
`Received` has no interface yet: typed messages, with pattern matching over a message type, are the next step for it.

## Projectiles and hitscan

A projectile belongs to its shooter: spawn it with its generated `Spawn`, and its plain `Node3D` root makes it
`Personal`. The shooter's peer moves it and decides every hit against the targets it displays: it pushes the target
and calls `Despawn()` in the same frame, so a projectile cannot pass through its first target or hit twice. To push a
crate with one, `this.Impulse(crate, impulse)` takes it and pushes it. `PlaygroundShot` is the worked example.

Hitscan needs nothing extra: the shooter runs an ordinary ray query. Bodies other peers simulate sit frozen at their
displayed positions, so the ray hits what the shooter sees.

## Spawning, despawn and teleport

- `Shot.Spawn(node, data)` and `Shot.Spawn(at: transform, data)`, generated for `[Scene]` and `ISpawnedWith<T>` classes,
  instance the class's scene on every peer and on late joiners; `OnSpawned(data)` runs everywhere before it enters the
  tree. See [Getting started](getting-started.md#spawning).
- `Despawn()` ends the object's timeline. The authority hides it and stops processing at once. Other peers keep
  showing it until their playback reaches the final sample, then hide it; the root is freed everywhere after a grace
  period. Do not `QueueFree` a replicated object yourself.
- `Snap()` makes the next sample apply without blending: a respawn, not a flight across the map.

## What is sent

`SyncedSummary` in the inspector lists it, in order:

- the root's full transform, for a 3D root (2D is not supported);
- velocity for a character body, linear and angular velocity for a rigid body;
- every `[Synced]` property of the root and its descendants, down to a nested `NetworkObject`.

Nothing else. State goes out 30 times a second (`NetworkObjectServer.SnapshotRate`), every other tick with the tick on the physics step (the default);
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
- **An adaptive buffer per link.** Depth is two send intervals plus that link's measured jitter, capped at 2/3 s.
  The buffer absorbs jitter; it cannot absorb latency, because nothing can be shown before it arrives.
- **No freezing, no rewriting.** On underrun the object holds its last value; after an outage playback catches up
  quickly. A late sample never rewrites what was already shown.
- **Objects start at their first sample.** A spawned object is hidden until playback reaches its first sample. A
  projectile starts at the muzzle, not hanging there or appearing down range.

`NetworkObjectServer.Instance.Diagnostics.GetPlaybackStatus(peer)` reports, averaged over a second, how old that peer's
state is on arrival and how long it waits in the buffer, in ticks and in milliseconds (`TotalMs`, `NetworkMs`, `PlaybackMs`). `Object.Diagnostics` has the sequences, the display tick and
the sample events, for checks rather than game logic.

## Authority change smoothing

When an object changes hands, every peer's copy of it jumps a little: the new authority simulates on from the newest
state it heard, which is already a ping old, and the others switch from one authority's samples to the other's. The
**Authority Change Smoothing** group on `NetworkObject` hides that jump. The body moves at once, so physics stays
right; what is drawn stays where it was on screen and catches up over **Smoothing Time**. A jump further than **Max
Smoothing Distance** is drawn at once, and so is anything after `Snap()`: those are moves, not lag.

It moves one node, **Visual**, so everything drawn has to sit under it and nothing physical may:

```
Crate (RigidBody3D)
├── CollisionShape3D       the body's shape: never smoothed
├── NetworkObject          Visual = Visual
└── Visual (Node3D)        an empty pivot: the node smoothing moves
    └── Model              the model: meshes, skeleton, AnimationPlayer
```

Keep the pivot empty and put the model inside it: then an animation that moves the model's own root (root motion) and
the smoothing never write to the same node. Visual empty means no smoothing. Pointed at the root itself, or at a node
outside the object, it is refused with an editor warning, since moving it would move the body.

Known limits, for now:

- **Ragdolls.** `PhysicalBone3D` is physics: under Visual it would be moved by hand for a moment. Keep physical bones
  out of Visual, or leave Visual empty on such an object.
- **IK aimed at the world.** A foot or a hand reaching for a point in the world reaches from where the model is drawn,
  so for the smoothing time a limb can stretch or bend by the size of the jump.
- **Bone hitboxes** (`BoneAttachment3D` with an `Area3D`) follow the drawn model while it catches up: hits land where
  the object is seen.

Players' characters never change hands, so none of this touches them; it is for shared objects such as crates.
