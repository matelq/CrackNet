# Distributed authority

The network model of the `distributed-authority` branch, and why. This branch leaves netfox's rollback model behind; the
`reworked` and `master` branches keep the netfox port.

## Target

Co-op "friend-slop": 4-8 friends, up to ~200 dynamic physics bodies, close contact between players (shared objects,
pushing each other, joint QTEs), physics and projectiles, fast and slow. Listen-server host over Steam. No cheating
concern: clients are trusted. **3D only** (decided): 2D roots are refused rather than kept as an untested copy of every
physics rule.

Tick 30 Hz, state snapshots 15-20 Hz. The host leaving ends the session; a late joiner gets a full snapshot of the world
and its owners. No host migration.

## Why not the other models

| Model | Why not here |
|---|---|
| Lockstep | Needs bit-exact determinism (Rapier and floats do not give it) and adds input delay. |
| Rollback (netfox) | Needs determinism or state correction, resimulates the physics space N ticks per frame (netfox-net#62), and still has to guess remote players. Contact between players never agreed (#44, #50, #51, #64). |
| Snapshot interpolation + prediction (Source, Overwatch) | Two timelines: my predicted player against everyone else in the past. Pushing each other and sharing objects is exactly where it is weakest. Server authority buys cheat safety we do not need. |
| Tribes / partial state | Built for bandwidth-starved huge worlds; objects can reach states the sender never had. |

What remains is Glenn Fiedler's distributed authority, from
[Networked Physics in Virtual Reality](https://gafferongames.com/post/networked_physics_in_virtual_reality/), which he
recommends for co-operative games only.

One rule sits above every mechanic: **every interaction has exactly one arbiter: the authority of the object that
initiated it; the host picks the first of opposing requests.** Check that rule explicitly whenever adding a mechanic.
If two peers can both decide the same contact, hit, grab or consumption, the mechanic is not finished.

## The model

**Authority and ownership.** Every networked object has an authority (the peer that simulates it and sends its state)
and optionally an owner (the peer holding it). Each has its own sequence number. A change applies at once on the
peer making it and goes to the host over a reliable channel; the host accepts it when (ownership, authority) is newer
and the object is free or already the requester's, tells everyone, and otherwise corrects the requester. Guests take
the host's word. State packets carry no sequences: a receiver keeps state only from the peer it knows as the
authority, and drops the samples it had when the authority changes, since they run on the previous peer's clock.
The current authority is Godot's `multiplayer authority` (`IsMultiplayerAuthority()` stays true); the sequences and
the ownership flag live on top of it. What counts as contact and rest is the game's, but the policy is the library's:
it calls `Touch` on contact and `ReturnToHost` once a body has settled.

- A player's own character is always authoritative on its peer. Input applies at once, with no reconciliation and no
  resimulation. Its authority never transfers.
- Grabbing a free object takes ownership optimistically; nobody else can take it until it is released.
- An authoritative object with `SpreadsAuthority` calls `Touch(other)`; its cause travels with the request, and the
  touched object follows its authority. `MaxSpreadDepth` limits the whole chain from its source (unlimited by default),
  rather than restarting at each crate.
- The host arbitrates conflicts (two grabs at once): the higher sequence wins, and an ownership change beats an
  authority change. For opposing contact chains, the first request takes the pair: the counter-request is rejected
  because its cause was just taken by the winner. The loser is corrected by the host's update.
- The host owns the world by default: NPCs, spawns, doors, anything at rest. A released object returns to the host
  after it has been at rest for N ticks.
- `Transferable` is part of the host's reliable authority record, including its own sequence, so runtime changes and
  the current value both reach every peer and late joiner.

**Remote objects.** An object whose authority is another peer is frozen locally (static, so a snap gives a character standing on
it no platform velocity) and is driven from a playback buffer. It does not push back until authority transfers to the peer touching it; no second physics runs on top of
received state.

**Playback.** One playback clock per remote peer, samples per object, so everything one peer sends (a stack of crates,
a character and what it holds) is shown at the same tick.

What a buffer can absorb is the send interval and jitter, never the base latency: nothing can be shown before it
arrives. So each link gets its own adaptive depth (decision: per-link buffer, not a session-wide common time):

- depth = minimum (send interval plus half a tick) + this link's jitter, capped at 20 ticks;
- jitter = 95th minus 5th percentile of how late each arrival came against local time, over the last 20 seconds of
  arrivals, by time rather than count; below 10 arrivals the minimum is used. The long window keeps the depth steady
  and makes it shrink slowly after a bad spell, while percentiles let a single outlier (the first packet, the one after
  an outage) pass. It still grows within about a second of sustained jitter;
- the clock slews toward the depth: 0.25 tick dead zone, down by at most 5%, up in proportion to how far behind it is,
  at most half again as fast. Stretching smooths moving between depths; it cannot stand in for depth, because a late
  stretch of packets lands faster than a few percent of speed can build a margin;
- the clock's own time keeps running while nothing arrives (a resting peer sends a heartbeat a second), so motion after
  a rest shows at the normal depth at once. The display never passes the newest sample: on underrun it holds, and
  after an outage it plays catch-up rather than adding lasting delay.

The playground shows per peer, averaged over a second, how old state is on arrival and how long it waits in the buffer. A late packet never rewrites a displayed past. Corrections are smoothed only in presentation, never in the
simulation.

State can arrive before a spawned object's reliable scene message. Receivers retain a bounded, short-lived set of
samples for unknown objects and apply them when the object registers. If those opening samples are already behind the
peer's shared display clock, the new object starts at its own first sample and advances faster until it catches that
clock. It never discards the start of its motion; once caught up, all objects from the peer share one clock again.

Despawn is also state. `NetworkObject.Despawn()` sends repeated final samples marked despawned during a grace period,
hides and stops the authority immediately, and delays freeing the spawner-owned node. Observers keep showing the
object until playback reaches the marked sample, then hide it; a reliable spawner removal may free it afterward.

**Players and contact.** Players do not collide with each other physically: each peer would push against the other in
the past. Instead:
- a push is an event "apply impulse X" to the pushed player's authority (`SendToAuthority`), applied as knockback in
  its controller; the pusher sees the result one round trip later. A guest whose own authority request is still
  unanswered holds events for the object until the host answers (the answer echoes the request's id), then raises
  them or passes them to the winner, so a claim the host rejects does not swallow an event;
- overlapping players are separated softly, each peer nudging its own character away from the displayed neighbour;
- standing on or carrying a player attaches to the carrier's displayed transform, like a held object (#59).

**Projectiles.** A projectile, fast or slow, belongs to its shooter and appears at once for them; other peers play it in
the shooter's timeline. The projectile authority is the one arbiter for every hit, against the targets it displays.
It sends damage or knockback to the chosen target with `SendToAuthority` and calls `Despawn()` on the projectile in the
same decision, so it cannot pass through the first of two targets or hit twice. Hitscan is likewise shooter-decided.
Physical projectiles (a grenade, a thrown crate) are ordinary physics objects. Spawning uses one Godot
`MultiplayerSpawner` per shooter, with the shooter as its authority. A remote projectile stays hidden until playback
shows its retained first sample, so it begins at the muzzle rather than hanging there or appearing down range.

**QTE.** The host announces a QTE with its start tick. Each participant judges its own input locally, relative to when
it saw the start, and reports the result; the host only collects and announces the outcome. The base primitive is
"press within a window together"; others are built from it and events.

**Topology.** The library addresses peers and never knows the route. The target Steam transport is a full mesh
(`SteamMultiplayerPeer` joins every lobby member), so state and peer-directed events go directly to their destination;
host-arbitrated authority records still go through peer 1. The playground mirrors that topology over ENet: a temporary
rendezvous lets the host assign compact peer IDs and tell a joiner about existing peers, then every gameplay pair owns
one `ENetConnection` in `ENetMultiplayerPeer.CreateMesh`. Its `MultiplayerPeerExtension` simulator applies a profile
once on each sending link—delay and jitter to all packets, steady and burst loss only to unreliable packets—so a
guest-to-guest packet is no longer relayed or charged twice.

**Bandwidth.** State goes out every 2 ticks (15 Hz). Values are written compactly (a type byte, floats). An object
whose state has not changed is sent only as a heartbeat once a second; a receiver that sees a sample after such a gap
holds the resting value until just before it, so the object starts moving when its authority did. Measured by
`BandwidthTests`: 50 moving and 150 resting objects cost about 136 kbit/s of state payload per peer (was 1.8 Mbit/s).
Delta against an acknowledged baseline, quantization per property and a priority accumulator (see Deferred) only once
measurements ask for them. State packets are filled up to 1200 bytes, under the MTU of any real route.

## API

One `NetworkObject` node per object: it holds the sequences and contact-spreading policy, sends state while
authoritative, and plays back and interpolates otherwise. Properties in its subtree are marked `[Synced]`;
`Interpolate` is true by default and set to false where a continuous value should step. Discrete types (bool, int,
enum, strings, references) always step. `Teleport()` makes the next snapshot apply without interpolation;
`Despawn()` ends the object's playback timeline. Games report contacts through `Touch`, not by reimplementing policy.

### Simplification (implemented on `simplified-api`)

Goal: a crate needs no code and a player needs only its own movement. Paid for in bytes where needed.

- **The library reacts to the root's type.** An unsupported root (`SoftBody3D`, `PhysicalBone3D` ragdolls) is an
  error on the node in the editor, and at run time the game logs it and quits before connecting.
- **What is sent, as a contract** shown read-only in the inspector ("Synced: transform, linear_velocity, ...") and
  as one table in the guide:
  - the root's full global transform, always, for any 3D root (no position-and-yaw variants for now:
    a character is thrown and tumbles too);
  - linear and angular velocity when the root is a `RigidBody3D`, velocity for a `CharacterBody3D`;
  - every `[Synced]` property of the root and its descendants, down to a nested `NetworkObject`;
  - nothing else: child transforms, animation, wheels, particles only when marked `[Synced]`;
  - changed state at the next send, unchanged once a second; applied on non-authority peers every frame, blended,
    at the sender's playback delay.
- **Kind** instead of `Transferable` / `SpreadsAuthority` flags, `Auto` by default from the root's type; named by the
  rule, not by an example:
  - `Personal`: authority stays with one peer and passes on contact (a player, a projectile, a grenade);
  - `Shared`: taken by touch or grab and passes on contact (a crate, a ball);
  - `World`: authority stays put and does not pass (a lift, a door, the match score) - not necessarily the host's;
  - `Custom`: the flags by hand. No preset for "shared but does not pass on contact".
  - `Auto` resolves `CharacterBody3D` and plain `Node3D` to `Personal`; `RigidBody3D` and `VehicleBody3D`
    to `Shared`; `AnimatableBody`, `StaticBody`, `Area` and non-spatial `Node`/`Control` to `World`. A grenade (a
    rigid body that stays its thrower's) is the common case that picks `Personal` by hand.
- **Built-in behaviour for physics roots:** freeze where not authoritative (with the Rapier re-set), contact
  monitoring and `Touch` on contact for rigid bodies, `Touch` on slide collisions for character bodies, return to the
  host at rest.
- **`Knock(Vector3)`** built in: an impulse on a rigid body's authority, a `Knocked` event on a character's. The
  general event stays.
- **One `Spawn(parent, scene, setup, authority)` call** instead of a `MultiplayerSpawner` per shooter: `setup` runs
  on the spawning peer before the root enters the tree, and the root's transform goes with the spawn.
  It sends the scene path, with no registry of spawnable scenes (clients are trusted). No separate spawn data: what a
  new object needs is `[Synced]`, and the object stays hidden until its first sample brings it.
- **Authority** in one place: `Authority.Peer`, `Authority.IsLocal`, and `Authority.Take()` / `Authority.ReturnToHost()`
  for what physics bodies do themselves, by hand, for `Custom` objects.
- **Grab:** `TryGrab()`, `Release()`, plus `Throw(Vector3 velocity)` so a throw's velocity is set by the library.
- **Events:** `Send(Variant)` / `Received(int from, Variant)` instead of `SendToAuthority` / `EventReceived`.
- **Diagnostics** out of the main API: sequences, `DisplayTick`, `SampleSent/Received` move to `Object.Diagnostics`,
  a peer's playback status to `NetworkObjectServer.Diagnostics`.
- **Removed:** `NetworkSchemas`, `PeerVisibilityFilter`; the public `NetworkTime` reduced to what games use.
- **Soft separation** of `Personal` character bodies built in and optional: `SeparationStrength` in the inspector,
  0 turns it off. Each peer nudges its own character away from the displayed neighbours.
- **Hitscan** needs no API: the shooter runs an ordinary Godot ray query, since non-authority bodies sit frozen at
  their displayed positions, and follows with `Knock` or `Send`. Documented in the guide.
- **QTE** is a playground example over `Send` and a `World` object, not a library node, until a second kind of QTE
  shows what a shared node should be.
- **Order:** implement this API and move the playground onto it first, then the remaining playground mechanics.

### Second pass (decided, not implemented)

Judged by `docs/examples.md`; the owner found sections 3 (players and objects) and 5 (shooting) hard to read.
Proposals came from independent Claude and Codex reviews.

- **`node.Net()`** extension instead of `GetNode<NetworkObject>("NetworkObject")` and `NetworkObject.Of(node)!`.
  Everyday calls become extensions on the game's own nodes, so game code rarely names `NetworkObject`.
- **`Push`** replaces `Knock`: `crate.Push(impulse)` delivers a push to whoever simulates the target (an explosion, a
  trap); `this.Push(crate, impulse)` is "my object struck yours": it takes the target by `Touch` when it can, then
  delivers the push, so a crate and a player are struck the same way. No `Try`: the push is always delivered. The
  event is `Pushed`.
- **`PushStrength`** on a character body's `NetworkObject`: the library pushes the rigid bodies the character slides
  into, along the contact normal. 0 is off, and a game that pushes along its input keeps its own loop.
- **`TakeKnockback(delta)`**: the object accumulates pushes for a non-rigid root; `Velocity += TakeKnockback(delta)`
  replaces a field, a subscription and a decay line. `Pushed` stays for a custom curve.
- **Owning and carrying:** `TryClaim()` (mine, nobody else may take it, frozen, the game moves it), `Release()`,
  `Throw(velocity)`, and `TryCarry(item, anchor)` where the item follows an anchor node of the carrier (a marker, a
  bone attachment), with `CarriedItems` on the carrier. Carrying belongs in the library, not in extras: only the
  library can place a carried item on each peer's own copy of the anchor instead of playing back its samples.
  See Carrying below.
- **Spawn** is generated per class: `Crate.Spawn(at: transform)` and `Shot.Spawn(Muzzle)` (from a node's transform);
  `parent` defaults to the current scene's root and `authority` to the calling peer. The scene is `<Class>.tscn` next
  to the script or `[Scene("res://...")]`. Data an object needs when created goes through `ISpawnedWith<TArgs>`:
  `OnSpawned(args)` runs on every peer and late joiner before the root enters the tree, and the arguments travel in
  the spawn message - `Player.Spawn(SpawnPoint, Colors.Blue, authority: 3)` makes the player blue on the peer that
  simulates it too, which a setup callback run only by the caller could not. The argument is optional only when
  `OnSpawned` declares a default value itself; a struct's implicit default does not count, so a forgotten fuse is a
  build error. The transform stays a library concern: it must be global and set before the first physics frame.
- **Analyzers:** a spawned type without a scene, a script used by two scenes, or an `OnSpawned` that does not match
  its `ISpawnedWith<T>` fail the build. `.tscn` files reach the analyzer as additional files.
- **Projectiles** need their own design pass (trajectories, not just straight lines) before a `NetworkProjectile`
  node is added.
- **Renaming the library** later must also rename the diagnostic ids (`NFX...`), namespaces and generated attributes.

### Carrying and animation (decided, next stage after the second pass)

From independent Claude and Codex research; the approach matches Fiedler's VR demo (a held cube stops being sent and
rides in the avatar state), Unity Boss Room and NGO `AttachableBehaviour`, Unreal `AttachmentReplication` and Fusion's
parent sync.

- **Carrying is state, not a transform stream.** While carried, an item sends no transform; its samples carry the
  attachment instead (carrier, anchor path relative to the carrier, offset). Every peer, the authority included, puts it
  on its own copy of the anchor after that peer's animation, so a hand animated locally from synced parameters and the
  item in it cannot drift apart.
- **The anchor is a node:** `this.TryCarry(item, Shoulder)` takes the `Marker3D` (under a `BoneAttachment3D` for a bone).
  The library sends its path relative to the carrier and resolves it on each peer; an anchor outside the carrier is an
  error.
- **Attach and detach switch at the carrier's playback time,** inside the sample stream. Keying presentation off
  `Holder`, which applies when the host's record arrives, would put the item in the hand one playback delay before the
  hand gets there.
- **Detach blends** the gap between the observer's anchor and the thrower's first free sample over about 0.1-0.2 s, in
  presentation only.
- **Carried players** keep their authority. The carried player's peer decides whether to accept being carried (first
  request wins); every peer, including a third one and the carried player's own, shows it on its own displayed carrier,
  or a third peer sees it doubly delayed. A throw is detach plus push. Cycles are refused.
- **Animation stays parameters:** games mark AnimationTree parameters `[Synced]`, applied at the display tick like any
  state. Root motion runs on the authority only; its result is the transform. One-shots (grab, throw) are a `[Synced]`
  counter incremented in the same tick as the change to the item, documented as a pattern, not new API. No pose sync.
- **Frame order:** an item is placed after the carrier's AnimationTree and bone attachments update, or the hand leads
  by a frame. To verify on Godot 4.7.2.
- **Deliberate gap: animation phase on late join.** A peer joining mid-animation starts looping animations from phase
  zero, since only parameters are replicated, not state and start tick. Accepted for now; revisit when a game shows it.
- **Tests first:** per frame on a remote peer during a carried walk with animation, item-to-anchor distance near zero,
  failing on today's carry before the change; attach and detach at the carrier's display tick; no jump during the
  detach blend; a third peer's carried player on its displayed carrier; a late joiner sees the item in the hand.

### Physics between peers (decided, in progress)

Every peer has its own bodies, dynamic and in the present, and copies of the others', kinematic and a playback delay
behind. Playtests and the smoke found three ways this breaks, each of which had been patched case by case: a kinematic
copy pushes a local body with infinite mass, so neither mass nor Rapier's corrective velocity limits the speed it
gives (a thrown crate's copy on the host shot the host's stack off); a contact happens at different times on
different peers, so authority moves after the hit instead of before it; and a body frozen or unfrozen at a pose the
other peer shows late overlaps what is around it.

- **Copies of Shared bodies do not collide with local Shared bodies** ("ghosts"). Only the peer simulating a body
  resolves its hits; authority still spreads by touch, found by overlap queries as when a body is taken. Copies keep
  colliding with characters, so a player can stand on one. Cost: during a transfer (one playback delay) a thrown crate
  may pass visibly into a stack on the host. Backup if that looks bad in playtests: simulate every body on every peer
  and pull copies towards the received state (Fiedler's VR demo), which gives finite-mass contacts at the price of CPU,
  correction jitter and a different playback model.
Two decisions here are made but not built, and both matter enough to keep in sight:

- **Base-relative positions for standing or riding** (with carrying, below: the same mechanism). A character's position
  is sent relative to the body it stands on, and every peer places it on its own copy of that body, so a player riding
  a crate or a lift does not slide against it by a playback delay. Since copies are frozen static, a player on a moving
  copy is not carried at all today, only shoved by it.
  - Whether a player rides at all stays the game's choice, through Godot's own `platform_floor_layers` and
    `platform_on_leave` on its `CharacterBody3D`: a game that excludes the crate layer keeps a player standing still on
    a crate rolling away. The library takes as the base whatever the engine already reports as that character's floor,
    so opting out of riding also opts out of the base, with no extra API. Riding stays the default: co-op wants lifts
    and crates to carry.
  - Per platform, by layer: lifts on one collision layer and crates on another, and a character's
    `platform_floor_layers` says which of them carry it. Per character too, since the mask is its own.
  - Per situation, by changing that mask at run time (not while carrying something, say). The engine reports the floor
    only after `MoveAndSlide`, so a choice made from what the character stands on applies from the next frame.
  - Godot has no exception for a single body among others on the same layer: "this crate carries me, that identical one
    does not" needs separate layers or a mask changed per situation.
- **Handover smoothing.** A launch is physics (infinite-mass pushes, kinematic velocity, depenetration) and is fixed as
  physics; what a handover can still leave is a visible snap of a metre or so, the playback delay's worth of position.
  Photon Fusion corrects that in presentation only, over about 0.15 s, and Fiedler blends extrapolation error the same
  way. To add when a playtest shows the snap rather than pre-emptively, and never by blending the physics body.
- Already in: a character does not take what it stands on; a group touched by any character does not go back to the
  host; Rapier's `normalized_max_corrective_velocity` is 2 in the playground project.
- **Copies are frozen static, not kinematic.** Rapier gives a kinematic body the velocity of its last move; a copy
  that snapped by 0.3 m launched a character standing on it 13 m up, and in a playtest 140 m. Riding a moving copy
  therefore no longer carries a player along until the base-relative position above is in.

## Tests the model needs

1. Two peers grab one object at once: exactly one owner, and every peer agrees who.
2. Authority chain: a thrown crate hits a second one, which follows the thrower.
3. Authority returns to the host after rest.
4. State from a peer that no longer has authority is not shown, nor blended into the new authority's.
5. Playback: one peer's objects show the same tick; underrun without freezing; a late packet does not rewrite the past.
6. A push event is applied exactly once, and an event follows an authority that moved while it was on its way, or
   that the host gave to someone else while the event was raised on a losing claim.
7. A projectile appears on other peers at its firing tick; its authority hits only the first of two players in line,
   and observers retain it until their playback reaches its despawn sample.
8. Late join: the full world and its owners.
9. Bandwidth bound: bytes per tick for N objects, failing on regression.
10. Three-process mesh smoke with `--profile=realistic`: an object driven by client A is observed by the host and
    client B, with disagreement measured on displayed positions during motion, not at rest.

## Playground

`examples/playground` is where the model gets played, and in time it has to exercise every point above. Iteration 2:

- [x] Players: always their own peer's, played back elsewhere, no collision between players
- [x] Push another player: an event to their peer, applied as knockback
- [x] Crates: frozen where not simulated, `Touch` spreads authority with host arbitration, back to the host at rest
- [x] Grab, carry, throw (ownership)
- [x] Slow projectiles: spawned and wholly arbitrated by the shooter; first hit knocks back and despawns on playback
- [x] Late join (MultiplayerSpawner plus the host's authority table)
- [x] A leaving peer's crates go back to the host
- [x] Full ENet mesh and per-link in-process simulation under named profiles
- [x] Three-process smoke (`--smoke`) including client-A-to-client-B state, comparing displayed positions during motion
- [x] Per-player delay readout split into network age and playback depth
- [x] The playground on the simplified API (crates without code, shots through `Spawn`)
- [ ] Soft separation of overlapping players (optional, built in)
- [ ] Hitscan (a plain ray query by the shooter)
- [ ] QTE: press together within a window (example over `Send`)
- [ ] Standing on, carrying and throwing a player: playtest the sketch in Deferred before deciding
- [x] Steam transport: host a friends-only lobby, join from the overlay or by lobby id, simulated profile on top
- [ ] Production-ready playground: scenes with proper node trees for every object and for the level instead of
  building them in code, and code clean enough to copy into a game

## Deferred

- Pusher-side predicted knockback shown as a decaying presentation offset. Revisit if push latency still bothers on
  Casual or Realistic after the mesh.
- Extrapolating targets to the present for hit tests, in the style of Photon Fusion "Forecast". Revisit if dodges do
  not count on Casual or Realistic.
- Sequence number overflow (review finding).
- Carrying a player, standing on one, and throwing one. Sketch: `AttachTo(other, offset)` / `Detach()` called by the
  carried player, which follows the carrier's displayed transform; a throw is a knock that also detaches, so
  authority over a player never moves. Needs playtesting before it becomes API; may stay game code.
- Typed events (`Send<T>` / `On<T>`) for several kinds of event per object without unpacking a `Variant`.
- Smaller transforms per root type (position and yaw for an upright character). Everything sends the
  full transform until measurements ask.
- Objects larger than a state packet. Today such an object warns and goes out oversized, fragmented by the transport.
  Supporting it properly means splitting a sample across packets or sending it through a reliable large-block channel
  (Gaffer on Games, Sending Large Blocks of Data). Revisit when a game needs one.
- A byte budget per packet with a priority accumulator (State Synchronization): what does not fit waits and gains
  priority. Revisit when bandwidth measurements ask for it.
- Quantization per property and delta against an acknowledged baseline (Snapshot Compression). A crate would drop
  from about 75 to about 21 bytes. Direction so far: off by default; the developer gives the world's size (an AABB,
  per project or per scene) and positions get their bits from it, while rotation (smallest three) and velocity do not
  depend on it. Whether attributes may override it per property is still open. The delta's baseline is also open:
  the latest snapshot the peer acknowledged (Gaffer on Games, Quake 3; needs acks and per-peer history, deltas stay a
  few ticks small) or a periodic keyframe such as the heartbeat (no acks, but deltas grow with time since it, and a
  lost keyframe blocks decoding until the next one).
- A common display time instead of per-link buffers. Session-wide: every screen shows every remote object at the same
  moment, set by the worst link, so one bad connection slows everyone. Per screen: each viewer uses the deepest of its
  own links, so only the players on a bad link pay. Revisit if playtests show objects of different players visibly out
  of step with each other outside interactions, which authority transfer already puts on one clock.
- Ghost pairs cost O(n) per authority change: every change of a shared 3D body visits every other shared body to set
  or clear its collision exception. Fine for dozens of crates; a spatial index (or per-island bookkeeping) when a game
  has hundreds.
- Seeding the jitter estimate before a match from the clock-sync pings, for a lobby that exchanges no object state.

## Sources

Gaffer On Games ([networking](https://gafferongames.com/categories/game-networking/),
[state synchronization](https://gafferongames.com/post/state_synchronization/),
[snapshot compression](https://gafferongames.com/post/snapshot_compression/),
[networked physics in VR](https://gafferongames.com/post/networked_physics_in_virtual_reality/)); Gabriel Gambetta,
[Fast-Paced Multiplayer](https://www.gabrielgambetta.com/client-server-game-architecture.html); SnapNet,
[Netcode Architectures](https://snapnet.dev/blog/netcode-architectures-part-1-lockstep/);
[Source Multiplayer Networking](https://developer.valvesoftware.com/wiki/Source_Multiplayer_Networking);
[Quake 3 network model](https://fabiensanglard.net/quake3/network.php);
[Peeking into VALORANT's netcode](https://technology.riotgames.com/news/peeking-valorants-netcode);
[Infil: netcode](https://words.infil.net/w02-netcode.html).
