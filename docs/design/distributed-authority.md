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

## The model

**Authority and ownership.** Every networked object has an authority (the peer that simulates it and sends its state)
and optionally an owner (the peer holding it). Each has its own sequence number. A change applies at once on the
peer making it and goes to the host over a reliable channel; the host accepts it when (ownership, authority) is newer
and the object is free or already the requester's, tells everyone, and otherwise corrects the requester. Guests take
the host's word. State packets carry no sequences: a receiver keeps state only from the peer it knows as the
authority, and drops the samples it had when the authority changes, since they run on the previous peer's clock.
The current authority is Godot's `multiplayer authority` (`IsMultiplayerAuthority()` stays true); the sequences and
the ownership flag live on top of it. What counts as touching and as rest is the game's: it calls `TryTakeAuthority`
on contact and `ReturnToHost` once a body has settled.

- A player's own character is always authoritative on its peer. Input applies at once, with no reconciliation and no
  resimulation. Its authority never transfers.
- Grabbing a free object takes ownership optimistically; nobody else can take it until it is released.
- Touching a free object takes authority over it, and whatever that object then hits follows, recursively: the peer who
  caused a collision simulates it.
- The host arbitrates conflicts (two grabs at once): the higher sequence wins, and an ownership change beats an
  authority change. The loser is corrected by the host's update. Conflicts are rare in practice even under latency.
- The host owns the world by default: NPCs, spawns, doors, anything at rest. A released object returns to the host
  after it has been at rest for N ticks.

**Remote objects.** An object whose authority is another peer is kinematic locally and is driven from a playback
buffer. It does not push back until authority transfers to the peer touching it; no second physics runs on top of
received state.

**Playback.** One playback clock per remote peer, samples per object, so everything one peer sends (a stack of crates,
a character and what it holds) is shown at the same tick. The clock runs a small fixed delay behind the newest sample
and slews its rate to hold that depth. On underrun it holds the last value or extrapolates briefly; it never freezes
to rebuffer. A late packet never rewrites a displayed past. Corrections are smoothed only in presentation, never in the
simulation.

**Players and contact.** Players do not collide with each other physically: each peer would push against the other in
the past. Instead:
- a push is an event "apply impulse X" to the pushed player's authority (`SendToAuthority`), applied as knockback in
  its controller; the pusher sees the result one round trip later;
- overlapping players are separated softly, each peer nudging its own character away from the displayed neighbour;
- standing on or carrying a player attaches to the carrier's displayed transform, like a held object (#59).

**Projectiles.** A projectile, fast or slow, belongs to its shooter and appears at once for them; other peers play it in
the shooter's timeline. A hit on my own player is decided by my peer, against what I see, so a dodge on my screen
counts. A hit on anything else is decided by the projectile's owner. Hitscan is decided by the shooter. Damage and
"projectile consumed" are events to the authority of the target or the projectile (`SendToAuthority`): reliable,
passed on if the authority moved on the way, and handled in arrival order there, so the first "consumed" wins. Physical
projectiles (a grenade, a thrown crate) are ordinary physics objects. Spawning and despawning use Godot's
`MultiplayerSpawner`, one per shooter with the shooter as its authority. A remote object stays hidden until playback
shows its first sample, so a projectile never hangs at the muzzle for the playback delay.

**QTE.** The host announces a QTE with its start tick. Each participant judges its own input locally, relative to when
it saw the start, and reports the result; the host only collects and announces the outcome. The base primitive is
"press within a window together"; others are built from it and events.

**Topology.** The library addresses peers and never knows the route. Over Steam every peer connects to every peer
(`SteamMultiplayerPeer` joins all lobby members), so state goes directly to observers; ownership, events and QTEs go
through the host. Over ENet, `SceneMultiplayer.server_relay` gives a star with the same code.

**Bandwidth.** State goes out every 2 ticks (15 Hz). Values are written compactly (a type byte, floats). An object
whose state has not changed is sent only as a heartbeat once a second; a receiver that sees a sample after such a gap
holds the resting value until just before it, so the object starts moving when its authority did. Measured by
`BandwidthTests`: 50 moving and 150 resting objects cost about 136 kbit/s of state payload per peer (was 1.8 Mbit/s).
Delta against an acknowledged baseline, quantization per property and a priority accumulator only once measurements
ask for them.

## API

One `NetworkObject` node per object: it holds the sequences, sends state while authoritative, and plays back and
interpolates otherwise. Properties in its subtree are marked `[Synced]`; `Interpolate` is true by default and set to
false where a continuous value should step. Discrete types (bool, int, enum, strings, references) always step.
`Teleport()` makes the next snapshot apply without interpolation.

## Tests the model needs

1. Two peers grab one object at once: exactly one owner, and every peer agrees who.
2. Authority chain: a thrown crate hits a second one, which follows the thrower.
3. Authority returns to the host after rest.
4. State from a peer that no longer has authority is not shown, nor blended into the new authority's.
5. Playback: one peer's objects show the same tick; underrun without freezing; a late packet does not rewrite the past.
6. A push event is applied exactly once, and an event follows an authority that moved while it was on its way.
7. A projectile appears on other peers at its firing tick; damage reaches the target's peer exactly once, including when
   the target decides.
8. Late join: the full world and its owners.
9. Bandwidth bound: bytes per tick for N objects, failing on regression.
10. Two-process smoke with `--profile=realistic`: an object handed from player to player, disagreement measured on
    displayed positions during motion, not at rest.

## Playground

`examples/playground` is where the model gets played, and in time it has to exercise every point above. Iteration 1:

- [x] Players: always their own peer's, played back elsewhere, no collision between players
- [x] Push another player: an event to their peer, applied as knockback
- [x] Crates: kinematic where not simulated, touching takes authority, the authority chain on impact, back to the host at rest
- [x] Grab, carry, throw (ownership)
- [x] Slow projectiles: spawned by the shooter, hits on crates decided by the shooter, on a player by that player
- [x] Late join (MultiplayerSpawner plus the host's authority table)
- [x] A leaving peer's crates go back to the host
- [x] Two-process smoke (`--smoke`) under a named profile, comparing displayed positions during motion
- [ ] Soft separation of overlapping players
- [ ] Standing on and carrying a player (attach to the displayed transform)
- [ ] Throwing a player: `Transferable` carried by the authority command, so the thrown player can hand itself over
- [ ] Hitscan
- [ ] QTE: press together within a window
- [ ] Steam mesh instead of the ENet star

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
