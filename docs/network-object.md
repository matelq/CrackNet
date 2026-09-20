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
| `Attached`, `AttachedTo` | The items hanging on this object, and what this object hangs on, as this peer shows them. |
| `PlaybackState` | `Pending` until playback here reaches the first sample, `Playing`, then `Ending` after a despawn. What a projectile checks before it hits someone, instead of `Visible`. |
| `AuthorityChanged` | Raised on every peer after authority or holder changed; for watching someone else's object. |

Every change is optimistic: it applies on the requesting peer at once and goes to the host, which accepts it or
corrects the requester. A request returns false when it cannot even be tried (someone else holds the object, it is
not transferable, or this peer is not connected); true means applied here and sent, not yet accepted.

For physics roots the library makes these calls itself: a rigid body touches what it hits while moving and returns to
the host at rest, a character body touches what it slides into. Games call:

| Call | When |
|---|---|
| `TryAttach(item, anchor)` | Hangs the item on a node under this object's root (a `Marker3D`, under a `BoneAttachment3D` for a bone): claimed, collisions off, placed on every peer's own copy of the anchor after that peer's animation. See [Carrying](#carrying). |
| `Detach(item)` | Takes it off and lets go; `Impulse` it right after to throw. |
| `TryClaim()` | The object becomes this peer's: authority and ownership, nobody else can take it. The body is frozen while claimed; move it by hand. For holding without an anchor: a lever, a crate dragged in place. |
| `ReleaseClaim(velocity)` | Lets go with a velocity: the throw flies on this peer's simulation. |
| `ReleaseClaim()` | Lets go without one. |
| `Spread(other)` | Contact the physics engine does not report: a projectile that moves itself, a melee swing. |
| `Authority.Take()`, `Authority.ReturnToHost()` | What physics bodies do themselves, by hand: for `Custom` objects. |

Conflicts are settled by the host. A grab beats a touch, and of two touches the first one to arrive wins.
`MaxSpreadDepth` limits how many objects a chain can pass authority through, counted from its source.

The rule to hold on to: **every interaction has exactly one arbiter**, the authority of the object that started it.
If two peers can both decide the same hit or grab, the mechanic is not finished.

## Carrying

An item in a hand is not a transform stream. While attached it sends no transform: its samples name the carrier and
the anchor, and every peer, the carrier's own included, puts it on its own copy of the anchor once per frame, after
that peer's animation and its bone attachments have moved. A hand animated locally from synced parameters and the
crate in it therefore cannot drift apart, and a held crate at rest in the hand costs a heartbeat a second.

- **The anchor is a node** under the carrier's root: a `Marker3D`, under a `BoneAttachment3D` for a bone. Its path
  relative to the carrier is what travels; an anchor outside the carrier is refused with an error.
- **Attach and detach are shown at the carrier's display tick.** The host's record of the claim arrives a playback
  delay earlier; keyed off it, the crate would sit in the hand before the hand got there. A one-shot animation (grab,
  throw) is a `[Synced]` counter bumped in the same tick as the `TryAttach` or `Detach`: an observer sees both in the
  same frame.
- **A held item passes through the world.** Moved by hand into wherever the hand is, a colliding body would land inside
  its neighbours and the engine would throw them out of the world; its collision layer and mask are 0 while attached and
  come back on `Detach`.
- **A throw is `Detach` and then `Impulse`.** Other peers draw the item leaving their own hand along the line to the
  thrower's first free sample, instead of jumping the difference between the two hands. An impulse on a carried
  player waits, whole, for the moment its own peer frees it: the impulse travels straight to that peer while the
  release goes through the host, and it must not fade meanwhile. On the thrower's screen a carried player leaves the
  hand when the player's own stream says so, a round trip through the host plus the playback delay after the call
  (about 270 ms at 100 ms one way): start the throw animation at the call, as the playground does, and it reads as a
  wind-up.
- **What you let go of stays let go.** A grab and a release quicker than the round trip do not put the player back in
  your hand when its stream from the grab reaches you: until its stream is past your release, the player is shown
  free where it is on your screen, and then leaves the hand for its first free sample.
- `TryAttach` is optimistic like `TryClaim`: it applies here and the host is asked. A carrier that wants to know when
  the item is really in its hand on this peer, or is told the host gave it to someone else, implements
  `IAttachmentChanged` and reads `Attached`.

```
Player (CharacterBody3D)
├── CollisionShape3D
├── NetworkObject
└── Visual
    └── Model
        └── Skeleton3D
            └── BoneAttachment3D (hand bone)
                └── Hand (Marker3D)        this.TryAttach(crate, Hand)
```

**Carrying a player** is the same call: `this.TryAttach(otherPlayer, Shoulder)`. The player keeps its authority. What
travels is a claim of ownership, arbitrated by the host like a grab, whose record names the carrier and the anchor;
when it reaches the carried player's own peer, that peer hangs its player from it, and its stream tells everyone, the
carried player's own screen included, where it rides that peer's displayed copy of the carrier. Of two players
reaching for one at once the first request wins. The carried player's controller skips its own movement while
`AttachedTo` is set; a throw is `Detach` and `Impulse`, which the player's own peer flies through `ImpulseVelocity`.
Either the carrier or the carried player may `Detach`; a carrier leaving the session puts the player down.

**Standing on a moving body** uses the same mechanism without a call. A character whose floor, as the engine reports
it after `MoveAndSlide`, is another object's body sends its position relative to that body, and every other peer puts
it on its own copy of the body: a player riding a crate or a lift is drawn on it everywhere rather than a playback
delay behind it. Nothing is claimed, collisions stay on and `Attached` does not list riders. A jump does not end the
ride: the character stays relative to the body through the air until it lands on something else, so a jump on a
lift lands where its own peer had it on every screen. The game opts out per layer through the character's
`platform_floor_layers`, as for the platform's velocity. Not in yet: on the rider's own peer a copy of a moving
body is a frozen static and does not carry the character standing on it.

**A moving platform with players on it is not first class**, and riding one has a cost worth knowing before you
build a level on it. A rider measures its place against its own copy of the platform, which trails the platform's
peer by the depth of that link, while every screen puts it on the platform as they draw it. The two are apart by
what the platform travels in that time - a metre at 150 ms and 3 m/s - and the difference appears either as a step
where the rider gets on and off, or as a drift along the platform while it rides. Which one you get is a choice,
not a defect to fix: the metres are conserved unless the rider and the platform are simulated on the same peer.
Slow platforms, or ones whose riders come from the peer that drives them, stay inside the noise.

## Animation

Animation is not sent. Every peer runs its own AnimationTree, and what travels is its inputs, marked `[Synced]` like
any other state and applied at the same display tick as the transform, so legs and body come from one sample. No pose
sync, no animation state.

```csharp
[Synced]
public float WalkBlend
{
    get;
    set { field = value; _tree?.Set("parameters/Walk/blend_amount", value); }
}
```

- **A loop** (walk, run, idle) is a blend parameter like the one above, set from the character's speed on the
  authority every physics frame.
- **A one-shot** (grab, throw, shoot, a flinch) is a counter bumped in the same tick as the action, so an observer
  plays it in the frame it sees the crate leave the hand. The setter plays the *difference* each sample brings, once
  the first value is known: two bumps inside one snapshot arrive as one step of two, and the first value a late joiner
  receives is history, played zero times. Several clips travel on one counter by sending the clip's index beside it
  in *one* value (`Vector2I`): two properties would arrive in no fixed order, and the counter would fire before the
  clip beside it had been applied. This is what Unity's `NetworkAnimator` calls a trigger, and for the same reason:
  a state that is true for an instant does not survive being sampled.
- **An item on a bone** goes on a `Marker3D` under a `BoneAttachment3D` and is placed after that peer's animation
  and the skeleton's own deferred pass, so it does not lag the hand by a frame. This is what the playground's crate
  rides, and what the smoke measures as the distance from the hand it should be in.
- **Aim at synced state**, a `[Synced] Vector3` or the anchor of a carried item, rather than at where this peer
  happens to draw another player: the two are a playback delay apart.
- **Not covered here yet**, because nothing in the repo exercises them and this guide does not teach what has not
  been run: root motion (#75), a hand moved by a `SkeletonModifier3D` (#76), and `AnimationNodeStateMachine`, whose
  state is an object rather than a parameter and so needs a pattern of its own (#77).
- **Not in: animation phase on late join.** A peer joining mid-loop starts it from phase zero.

The playground's player is the worked example: `WalkBlend` and `Gesture` in `PlaygroundPlayer.cs`, the tree in
`PlaygroundPlayer.tscn`. Its flinch shows where a one-shot comes from when the action happened elsewhere: the shove
arrives as a push, `IImpulsed.OnImpulsed` fires on the pushed player's own peer, and the gesture it bumps there travels
back out like any other state.

One Godot trap, worth knowing before you build the tree: an `AnimationTree`'s nodes are a **resource**, shared by
every character instanced from the scene, and only the tree's *parameters* belong to the single character. So give
each clip its own node and fire it through its own parameter; do not point one node at a different clip at runtime,
or every other character plays it too. (`resource_local_to_scene` does not help here - `tree_root` stays shared,
godotengine/godot#89353.)

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
| `IAttachmentChanged.OnAttachmentChanged()` | on every peer, on the carrier and the item, when that peer shows the attachment change | `Net().AttachmentChanged` |
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
- **One clock per peer, not one per screen.** Each peer's objects are shown at that peer's own depth, so a screen
  holds as many moments as it has peers and nobody pays for anyone else's link. Two objects from different peers
  that touch - a player and the platform under it - are therefore places at two moments, which is what
  "Standing on a moving body" above is not first class about. Shown at one time instead, every player on the screen was drawn
  at the depth of the worst live link: a 25 ms player went from 117 ms behind to 695 ms behind with a single 300 ms
  player present. That is the trade, and it was taken this way.

`NetworkObjectServer.Instance.Diagnostics.GetPlaybackStatus(peer)` reports, averaged over a second, how old that peer's
state is on arrival and how long it waits in the buffer, in ticks and in milliseconds (`TotalMs`, `NetworkMs`, `PlaybackMs`). `Object.Diagnostics` has the sequences, the display tick and
the sample events, for checks rather than game logic.

## Authority change smoothing

When an object changes hands, every peer's copy of it jumps a little: the new authority simulates on from the newest
state it heard, which is already a ping old, and the others switch from one authority's samples to the other's. The
**Authority Change Smoothing** group on `NetworkObject` hides that jump. The body moves at once, so physics stays
right; what is drawn stays where it was on screen and catches up over **Smoothing Time**. A jump further than **Max
Smoothing Distance** is drawn at once, and so is anything after `Snap()`: those are moves, not lag.

One case moves the body itself rather than the drawing. Normally what a peer was showing of the old authority becomes
the first sample of the new authority's track and playback runs from there, which needs the two to be in order on one
timeline. When they are not - the tick carried over is later than the new authority's first sample, or the two are
further apart than **Max Smoothing Distance** - the body used to be put on that sample in one frame. It now slides
there over **Smoothing Time** instead, and a second handover arriving mid-slide adds to the remaining distance rather
than restarting it. Two players shooting one crate faster than a round trip on links ten times apart drew teleports
of 2.0-2.4 m before and 0.06-0.16 m after. The cost is that while it slides, the body is deliberately off the line
between the samples that were received.

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
