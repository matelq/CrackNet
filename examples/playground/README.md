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

Two things worth knowing. Rapier's single build per dimension is already cross platform deterministic - there is no
determinism option to hunt for. And a rewind steps the whole space again for every tick of the range, so it is much
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
