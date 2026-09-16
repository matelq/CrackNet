# Distributed authority

The network model of the `distributed-authority` branch, and why. This branch leaves netfox's rollback model behind; the
`reworked` and `master` branches keep the netfox port.

## Target

Co-op "friend-slop": 4-8 friends, up to ~200 dynamic physics bodies, close contact between players (shared objects,
pushing each other, joint QTEs), physics and projectiles, fast and slow. Listen-server host over Steam. No cheating
concern: clients are trusted.

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

**Remote objects.** An object whose authority is another peer is kinematic locally and is driven from a playback
buffer. It does not push back until authority transfers to the peer touching it; no second physics runs on top of
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
Delta against an acknowledged baseline, quantization per property and a priority accumulator only once measurements
ask for them.

## API

One `NetworkObject` node per object: it holds the sequences and contact-spreading policy, sends state while
authoritative, and plays back and interpolates otherwise. Properties in its subtree are marked `[Synced]`;
`Interpolate` is true by default and set to false where a continuous value should step. Discrete types (bool, int,
enum, strings, references) always step. `Teleport()` makes the next snapshot apply without interpolation;
`Despawn()` ends the object's playback timeline. Games report contacts through `Touch`, not by reimplementing policy.

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
- [x] Crates: kinematic where not simulated, `Touch` spreads authority with host arbitration, back to the host at rest
- [x] Grab, carry, throw (ownership)
- [x] Slow projectiles: spawned and wholly arbitrated by the shooter; first hit knocks back and despawns on playback
- [x] Late join (MultiplayerSpawner plus the host's authority table)
- [x] A leaving peer's crates go back to the host
- [x] Full ENet mesh and per-link in-process simulation under named profiles
- [x] Three-process smoke (`--smoke`) including client-A-to-client-B state, comparing displayed positions during motion
- [x] Per-player delay readout split into network age and playback depth
- [ ] Soft separation of overlapping players
- [ ] Standing on and carrying a player (attach to the displayed transform)
- [ ] Throwing a player: `Transferable` carried by the authority command, so the thrown player can hand itself over
- [ ] Hitscan
- [ ] QTE: press together within a window
- [ ] Steam transport (the playground mesh exercises the intended route over ENet)

## Deferred

- Pusher-side predicted knockback shown as a decaying presentation offset. Revisit if push latency still bothers on
  Casual or Realistic after the mesh.
- Extrapolating targets to the present for hit tests, in the style of Photon Fusion "Forecast". Revisit if dodges do
  not count on Casual or Realistic.
- Sequence number overflow (review finding).
- A common display time instead of per-link buffers. Session-wide: every screen shows every remote object at the same
  moment, set by the worst link, so one bad connection slows everyone. Per screen: each viewer uses the deepest of its
  own links, so only the players on a bad link pay. Revisit if playtests show objects of different players visibly out
  of step with each other outside interactions, which authority transfer already puts on one clock.
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
