# Playground

A small complete game to copy from: host or join by address, one player per peer, movement with rollback and
prediction, and a platform to ride.

```
<godot> --path . res://examples/playground/playground.tscn
```

Run two instances (Debug > Customize Run Instances in the editor), press **Host** in one and **Join** in the other.
Never done this before? **[TESTING.md](TESTING.md)** walks through it from nothing, including how to make it misbehave
on purpose so you can see rollback working.
Arrows or WASD to move, Space to jump twice, Enter to shoot. The status line at the top shows the tick and the range the last
rollback covered.

## What to look at

**Everything netfox needs is in the scenes, not in the scripts.** `player.tscn` carries the `RollbackSynchronizer`
with its property paths and the `TickInterpolator`; `Playground.cs` only creates the peer and spawns players. That is
the division a game would use, and it is what makes the editor side of the API - exported paths, configuration
warnings, the gather step - part of the sample rather than an afterthought.

**Two ways to declare a property, both in `player.tscn`.** `PlayerCharacter.JumpsLeft` and `JumpHeld` carry
`[RollbackState]`, so the synchronizer gathered their paths from the properties themselves and a rename would carry
them along. `:position` and `:velocity` belong to `CharacterBody3D`, so there is nowhere to hang an attribute and they
are listed as strings. The synchronizer takes both.

**`JumpHeld` is the interesting one.** Edge detection - "was jump pressed this tick and not the last one" - needs the
previous input, and during a resimulation "previous" means the tick being resimulated, not the latest one. So it has
to be rollback state like anything else. Drop it from the state list and double jumps start misfiring after a
correction.

**The player's movement lives in a `RewindableStateMachine`.** `Grounded` and `Airborne` are children of it, and the
machine's current state is in the synchronizer's state properties - so a rewind puts the player back in the state it
was in for that tick and replays the transitions from there. An ordinary state machine would keep whatever state the
mispredicted future left it in, which is the whole reason this one exists.

**Shooting is not rollback, and that is deliberate.** `PlayerWeapon` is a `NetworkWeapon3D`: the shooter spawns a
projectile the moment it fires, and the authority independently spawns its own and compares the two spawn transforms.
Close enough and the shot stands; too far apart and the shooter's copy is taken back. That is the cheap answer to "I
want shooting" - full rollback of projectiles is the expensive one, and netfox does not do it. The toolkit also never
despawns projectiles, so `Projectile` gives up after a fixed number of ticks by itself.

Note where the firing happens: in `_Process`, on the peer that owns the input, and not in a rollback tick. The weapon
sends an RPC, and a rollback tick runs again for every resimulated tick - which would fire again each time.

**The tolerance on a shot has to cover the round trip.** `NetworkWeapon3D` accepts a shot when the shooter and the
authority agree about where it came from within `DistanceThreshold`. The default is one metre, and at 5 m/s with ten
ticks of lag a player has already moved 1.6 - so ordinary shots get rejected. The sample sets three.

**Not everything replicated is rollback state.** The scoreboard is a `StateSynchronizer`, not a
`RollbackSynchronizer`: the host decides it, it only goes up, and no rewind should ever take a shot back. The rule of
thumb is whether resimulating a tick could legitimately produce a different value. If it could, it is rollback state;
if not, a StateSynchronizer is less machinery and cannot be corrected into something surprising.

**The beacon is only replicated to players standing near it.** Its `PeerVisibilityFilter` defaults to invisible and
adds one distance check per peer, recomputed every tick loop rather than on join - because who can see it depends on
where people are, not on who is in the game. Without filtering, a client receives everything and no amount of hiding
it on screen changes that.

`Beacon._Process` runs that same distance check locally and hides the mesh when it fails, so a distant client sees
nothing rather than a frozen sphere. The frozen sphere is what filtering actually looks like from the inside - the
last position this peer was ever told about - and hiding it is what makes the filter legible instead of looking like
a replication bug.

**The player carries a network schema.** `PlayerCharacter.ApplySchema` tells netfox how to encode each property
rather than leaving it on the general-purpose variant encoding, which carries a type tag per value and sizes
everything for the worst case. Both peers have to agree, which is why it is code both run rather than a scene
setting. Only the input direction is lossy: half precision on a value that never accumulates is invisible, while a
velocity that gathers gravity over many ticks is not the place for it.

**The status line shows the netfox monitors.** `props sent/full` is what diff states buy: only the properties that
changed go out.

**The platform is what makes misprediction visible.** `MovingPlatform` computes its position from the tick rather
than from an accumulator, so a resimulated tick puts it exactly where the first pass did. Change it to accumulate
`delta` instead and ride it: a correction will drag the player off.

**Players agreeing about each other is checked separately.** `ConvergenceSmoke.tscn` runs one process per peer, walks
every player into the same spot, and then compares what each peer believes on one and the same tick - position, state,
jumps left, scoreboard - over the whole quiet window after the keys are released. Comparing by tick rather than by
wall clock is what makes it meaningful with the platform in play, since the platform never stops.

```
godot --headless --path . res://examples/playground/ConvergenceSmoke.tscn -- --host --profile=hostile
godot --headless --path . res://examples/playground/ConvergenceSmoke.tscn -- --join --profile=hostile
```

`--profile` picks a named set of link conditions instead of the constant delay and even loss `--latency` and `--loss`
give, which no real network does either of. `realistic` is
[what mas-bandwidth recommends playtesting above](https://mas-bandwidth.com/what-is-lag/) - 50ms round trip, jitter,
1% steady loss and a burst every few seconds - and `hostile` is deliberately worse, because a floor to play above is
not the same thing as a check. The run prints what the link actually did (`forwarded`, `dropped`, `burst_dropped`), so
a burst that never fired cannot be mistaken for a clean run.

The crates are compared too, by name like the players, and the players are steered into the middle one so it gets
shoved - that is the physics rollback being exercised by late input rather than merely present. A crate is
host-simulated and never predicted, so the expectation is the tight one, and it holds: `unsettled=0` for crates and
players alike. Walking into a crate pushes it because `PlayerCharacter.Move` applies an impulse on contact, inside the
tick and from this tick's velocity only - `MoveAndSlide` on its own never moves another body.

**Picking up a crate, carrying it and throwing it** is the generic "take a thing" mechanic, and the sample's first
real use of `RewindableAction`. Tab grabs the nearest crate, Shift+Tab throws it. While held the crate is not
simulated at all - it is frozen and placed relative to the player every tick, so the player's own prediction carries
it and no round trip is involved. The two transitions are `RewindableAction`s created in `PlayerCharacter._Ready`:
peers predict them in their rollback tick, the host broadcasts what really happened, and a cancelled pickup rolls the
crate back to free. `HeldCrate` is rollback state (an index, not a node reference), the crate is
`NetworkRollback.Mutate`d on each transition because it has no input of its own, and the throw velocity comes from
`Facing` - also rollback state - so a throw is a function of the tick. `ConvergenceSmoke --pickup` does the whole
thing on both peers and reports `held_ticks`, so a run that never actually grabbed cannot pass as one that did.

**The NPC** (`Npc.cs`, one `Npc_0` under `World/Npcs`) is the thing nobody controls: it wanders and runs from players
who come close. Its "AI" is a steering function in `RollbackTick` - a pure function of state, so the host can roll
it back when a player's late input turns out to have chased it another way. Authority is the host and prediction is
off, so clients never simulate it: they are told where it is and show that, interpolated, one round trip late. That
is the honest answer for an object nobody owns, and `ConvergenceSmoke` fails if a client ever simulated it.

`--peers=3` waits for three players before starting, and is then given to all three processes. Two peers is the one
count at which every player is either your own or the host's, so nothing has to be relayed on anyone else's behalf -
which is why a third process finds things the second never could. `--on-platform` puts everyone on the moving
platform, where it still fails ([#41](https://github.com/matelq/netfox-net/issues/41)), and `--dump` has every peer
write out each tick it recorded, which is how two runs get diffed.

**Riding it is done by hand, and has to be.** `PlayerCharacter` sets `PlatformFloorLayers = 0` to switch off Godot's
own moving platform support, and `RideFloor` adds `MovingPlatform.MotionAt(tick)` instead. Godot's version derives
the platform's velocity per physics frame and applies it inside every `MoveAndSlide`; a rollback runs many ticks per
frame and calls MoveAndSlide twice per tick, so that motion lands several times over and throws the rider off the
end. `PlatformRideCheck.tscn` is a headless check that it does not regress - it fails if the gap between player and
platform drifts by more than 5 cm over 150 ticks and a forced resimulation.

## Tiers

The base tier needs nothing installed and is what CI runs. The rest light up at runtime when their dependency is
there, and the status line at the top says which ones did.

### Rapier: rolling back real physics

Install [godot-rapier-physics](https://github.com/appsinacup/godot-rapier-physics) into `addons/godot-rapier3d` and
set `physics/3d/physics_engine="Rapier3D"`, and the playground adds a `RapierPhysicsDriver3D` and a row of crates you
can push around.

This is the one thing stock Godot cannot do at all. Rollback advances the game several times inside one frame, and
Godot's physics server only steps in `_PhysicsProcess` - there is no way to step it by hand
([PR 76462](https://github.com/godotengine/godot/pull/76462) is not in a release). Rapier exposes manual stepping and
whole-space snapshots, so crates get rewound and resimulated like anything else.

Two things worth knowing. Rapier is **locally** deterministic - same build, same machine, same run - which is all
netfox asks of it, since it replicates state rather than replaying inputs. Cross platform determinism is a different
claim and needs its `enhanced-determinism` feature, which a downloaded binary does not carry. And a rewind steps the
whole space again for every tick of the range, so it is much
more expensive than kinematic rollback: the tier uses one physics step per tick rather than the driver's default two,
and a long resimulation with many bodies will make itself felt.

### Steam: hosting through a lobby

Install the [GodotSteam GDExtension](https://codeberg.org/godotsteam/godotsteam) into `addons/godotsteam`, put
`steam_appid.txt` next to the executable (480, Spacewar, for development) and run with the Steam client open. The
"Steam host/join" button then creates a lobby and hosts inside it, and the status line shows the lobby id to share.
Paste a lobby id into the address field instead and the same button joins that lobby - Steam overlay invites are not
wired up, so the id is passed by hand.

netfox itself does not change: once a peer is assigned to `Multiplayer.MultiplayerPeer`, everything behaves as it
does over ENet. The extension is driven through `ClassDB` rather than through C# bindings, the same way the Rapier
driver is.

## Latency and loss

Turn on **Project Settings > Netfox > Autoconnect** and the `NetworkSimulator` in the scene connects the editor's
extra instances by itself, no lobby needed. The same section has simulated latency and packet loss, routed through a
local UDP proxy - the honest way to see what prediction is actually for.

## Checking it without a window

`PlaygroundSmoke.tscn` drives the same scene headless, so the sample cannot rot unnoticed. The host has to outlive
the client:

```
<godot> --headless --path . res://examples/playground/PlaygroundSmoke.tscn -- --host --seconds=14
<godot> --headless --path . res://examples/playground/PlaygroundSmoke.tscn -- --join --seconds=8
```
