# Playground

A small complete game to copy from: host or join by address, one player per peer, movement with rollback and
prediction, and a platform to ride.

```
<godot> --path . res://examples/playground/playground.tscn
```

Run two instances (Debug > Customize Run Instances in the editor), press **Host** in one and **Join** in the other.
Arrows or WASD to move, Space to jump twice. The status line at the top shows the tick and the range the last
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

**The platform is what makes misprediction visible.** `MovingPlatform` computes its position from the tick rather
than from an accumulator, so a resimulated tick puts it exactly where the first pass did. Change it to accumulate
`delta` instead and ride it: a correction will drag the player off.

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
