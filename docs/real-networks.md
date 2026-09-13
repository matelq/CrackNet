# Rollback on a real network

Everything in the other guides is true on a LAN. This one is about what changes once two people are playing over a
real connection, and it is written from things that actually went wrong in this repository, not from theory. Each
section names the failure, the reason, and what the library does about it now - and, where it is still open, says so.

## There is no server, only authority

The first thing that catches people out is looking for the server. netfox has no server code path. Godot gives
every `Node` a `MultiplayerAuthority` - an integer peer id, default `1` - and netfox reads exactly that, node by
node. The peer that is a node's authority is the peer whose simulation of it **counts**; every other peer predicts
and is corrected towards it. The only place netfox asks `IsServer()` is the clock.

So a listen server is simply "every state node has authority 1", and one player is two authorities on two nodes:

| Node | Authority | Because |
|---|---|---|
| the body (`:position`, `:velocity`) | peer 1 | the truth about where the player is lives on the host |
| the `Input` child (`Input:Movement`) | that player | only they can press their keys |

Why you would ever change it: to decide where the truth about **one object** lives. A pickup whose position should
follow whoever holds it does not need this - derive its position from the holder's and the holder's prediction
covers it for free. A thrown object does not either - its whole flight is a function of the release tick. What
does need it is an object a client should touch with zero delay while it is still simulating freely, and in a
co-op game that is allowed. Changing it at runtime works: the synchronization server re-sorts what it owns against
live authority once a tick (it used to read authority only at registration, and the peer that gained a node
recorded its state as real without ever sending it).

## Prediction is a promise with conditions

Prediction is exact while you move alone through a static world. It stops being exact the moment you touch
something driven by someone else, because resolving a contact needs both bodies at the tick being resolved, and a
peer that is guessing the other's input does not have that. The 2004 reference names the limit directly: prediction
works *"only if there is a clear ownership of objects by clients and these object interact mostly with a static
world"*.

What that looks like in practice, all measured in this repository:

- Two players who walk into each other on flat ground with no latency **agree exactly** - the contact is symmetric
  and `MoveAndSlide` separates them the same way on both peers.
- Three players converging on one spot **disagree permanently** about one run in five, by up to 30cm. A player
  wedged between two others has nowhere to slide, so it stops at whatever depth it happened to reach, and each peer
  reached a different depth because each predicted the two remote inputs differently. Nothing ever pushes them
  apart again: both peers are stable, in different places. Two players cannot be squeezed from two sides, which is
  why a two-player test reads a clean `0.0000` every time and proves nothing about this.
- A player standing on a moving platform that is blocked by another body is carried out from under itself, by a
  different amount on each peer.

The rule that follows: **your two-player test is not evidence about contact.** Anything about how players interact
with each other rather than with the world needs at least three peers, because at two every player is either your
own or the authority's and nothing is ever relayed on anyone else's behalf. The loopback harness under `test/Harness`
supports any number of stacks for exactly this, and `ConvergenceSmoke --peers=3` runs the sample with three.

One default worth knowing here: **a rollback node with no input is simulated on every peer.** netfox reasons that
a rule which needs no input can be run by anyone, and for a platform on a fixed path that is exactly right. For
something that reacts to players - an NPC, prey - it means every client extrapolates it from its own predicted view
of the players and is corrected each tick. If you want such an object replicated rather than guessed, gate its tick
on `IsMultiplayerAuthority()`: clients then keep the state they were sent and interpolate it. The sample's `Npc` does
this, and `ConvergenceSmoke` fails if a client ever ran its rule.

Whether remote players should be predicted at all, or shown a few ticks in the past with interpolation instead, is
open in this repository (netfox-net#38). For an object nobody owns - prey, a ball - the honest choice is available
per object: leave `EnablePrediction` off on its synchronizer and it is replicated and interpolated, never guessed.

## Test under a real network, not a LAN

A LAN playtest proves that your code runs. It says nothing about the two things a real link does: **loss comes in
bursts**, and **latency varies**. Independent loss at 10% takes three packets in a row one time in a thousand, so an
input redundancy of three covers essentially every gap it makes; a 300ms outage takes all three every time. And a
delay that never varies never stresses arrival order, because it is perfectly predictable.

`NetworkSimulator` models both, and has two named profiles:

- `Profile.Realistic` is what [What is lag?](https://mas-bandwidth.com/what-is-lag/) recommends playtesting above:
  50ms round trip, 20ms jitter, 1% steady loss, a burst every few seconds.
- `Profile.Hostile` is deliberately worse: 120ms each way, 10% loss, 40ms jitter, 300ms bursts. It exists because the
  recommendation is a floor to play above and a check wants something that fails when the code is wrong.

Measured, and worth knowing: the desync this repository spent longest on (netfox-net#35) **passes `Realistic` every
time** and fails `Hostile` two runs in three. The trigger was steady loss with latency past the input delay - not
bursts, and not latency alone. Guessing which conditions reproduce a bug is how it stays unreproduced.

The proxy counts what it forwarded, dropped and burst-dropped, and `ConvergenceSmoke` prints it. A configured burst
that never fires reads exactly like a clean run, and this repository has had two checks pass for that kind of reason.

```
godot --headless --path . res://examples/playground/ConvergenceSmoke.tscn -- --host --profile=hostile
godot --headless --path . res://examples/playground/ConvergenceSmoke.tscn -- --join --profile=hostile
```

## Input is repeated until it is acknowledged

A peer used to send the last three ticks of input in every packet. Against independent loss that is generous;
against a burst it is nothing, and the authority is left predicting for the length of the outage with no way to find
out otherwise afterwards.

Each peer now tells the peers sending it input how far it has got through, and they send everything past that -
floored at `netfox/rollback/input_redundancy`, capped at `netfox/rollback/max_input_redundancy` so one packet still
fits. This is what [Deterministic Lockstep](https://gafferongames.com/post/deterministic_lockstep/) prescribes: never
retransmit, keep including what has not been acknowledged. A 300ms burst that left seven ticks the authority never
found the truth for leaves none.

What is acknowledged is the newest tick below which nothing is missing, not the newest tick received. If 10, 11 and
13 arrive, acknowledging 13 would tell the sender to stop repeating 12, which never came. An acknowledgement may be
late; it must never be wrong.

Cost: 6% more input traffic when nothing is wrong, about double under 100ms latency - the window covers a round trip
of unconfirmed input, because a round trip is how long it takes to learn that it arrived. Input is still smaller
than state either way.

## A lossy schema is part of your simulation

A schema used to be applied on the wire alone, so the peer owning a property simulated from the exact value and
everyone else from the quantized one. Half precision is about 5e-4 relative - a couple of centimetres over eight
seconds of walking - but it is the same error every tick rather than noise, so it never averages out, and the owner
and the authority disagree by a little for good.

History now records what the wire would deliver, so every peer simulates from the same number. The consequence for
you is in [Network schemas](network-schemas.md): picking `Degrees8()` for a facing means every peer, the owner
included, simulates from an angle rounded to 1.4 degrees. That is usually what you want, and it is never a surprise.

## Rolling back physics needs a physics driver

A correction re-runs several ticks inside one frame. For a `CharacterBody3D` whose movement you wrote in
`RollbackTick`, that is your own arithmetic run again. For a `RigidBody3D` the movement is the solver's, and the
engine steps the whole space once per physics frame on its own schedule - it cannot be told to advance one tick, now,
eleven times. And restoring a body's transform and velocity is not restoring the simulation: the engine holds contact
manifolds, solver caches and sleeping islands you cannot see, so a body put back where it was still remembers a
different past and produces a different future.

`PhysicsDriver` snapshots and rewinds the **whole space** and steps it on demand. Stock Godot has no way to step a
space, so `GodotPhysicsDriver3D` reports itself unavailable; `RapierPhysicsDriver3D` works against the
[Rapier](https://github.com/appsinacup/godot-rapier-physics) extension, and this repository's own project runs on it.
Measured: a rigid body rolled back ten ticks and resimulated comes out at exactly the same height.

Two things Rapier does not give you. It does not resolve contact between kinematic bodies - `MoveAndSlide` never
pushes another body whatever solver is underneath, so the wedging above is unchanged with the engine switched. And it
is deterministic **locally**, not across platforms, unless built with `enhanced-determinism`, which the prebuilt
releases are not. For an object clients never simulate that does not matter; for one they predict, a client on
another platform is systematically slightly wrong. In rollback with an authoritative host the prediction lives one
round trip before it is replaced, so the divergence has little time to grow - but it is a thing to measure rather
than assume, and it has not been measured here (netfox-net#54).

## Still open

- **Interpolate or extrapolate remote players** (netfox-net#38), and what `input_delay` buys (netfox-net#42). Both
  wait on a prototype of player-to-player pushing, because pushing is the case that decides them.
- **Corrections are shown as they land.** Nothing smooths a correction today; `TickInterpolator` smooths between
  ticks. The standard answer is a visual error offset that decays in rendering and never touches the simulation
  (netfox-net#39). Not built, because nobody has yet seen the pop it would hide.

## Reading

The reasoning above is traceable to these, in the order they are worth reading:

- [What every programmer needs to know about game networking](https://gafferongames.com/post/what_every_programmer_needs_to_know_about_game_networking/) - the model netfox implements, and its limits
- [What is lag?](https://mas-bandwidth.com/what-is-lag/) - what players call lag, and the playtest profile
- [Choosing the right network model for your multiplayer game](https://mas-bandwidth.com/choosing-the-right-network-model-for-your-multiplayer-game/) - where "client-side prediction with remote simulation" sits among the alternatives
- [Networked Physics (2004)](https://gafferongames.com/post/networked_physics_2004/) - the ownership condition on prediction, stated in one sentence
- [State Synchronization](https://gafferongames.com/post/state_synchronization/) - quantize on both sides; jitter buffers; what packet clumping does
- [Snapshot Interpolation](https://gafferongames.com/post/snapshot_interpolation/) - the alternative to predicting remote objects
- [Deterministic Lockstep](https://gafferongames.com/post/deterministic_lockstep/) - send everything unacknowledged; why floating point across platforms is the hard part
- [Floating Point Determinism](https://gafferongames.com/post/floating_point_determinism/) - the long version of that last point
- [Networked Physics in Virtual Reality](https://gafferongames.com/post/networked_physics_in_virtual_reality/) - authority that moves with interaction
