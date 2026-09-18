# Testing on a real network

A LAN playtest proves that the code runs. It says nothing about what a real link does: latency, jitter that bunches
packets together, and loss that comes in bursts. Everything that distributed authority is meant to handle - contact
across peers, hand-overs, projectiles hitting what the shooter saw - only shows up under those conditions.

## Simulated conditions

`NetworkSimulator` wraps a `MultiplayerPeer` in `SimulatedMultiplayerPeer` and applies a profile once on each sending
link. Delay and jitter apply to every packet; steady and burst loss apply only to unreliable packets, since the
transport resends reliable ones. Latency is each way.

| Profile | Latency | Jitter | Loss | Bursts | Use |
|---|---|---|---|---|---|
| `clear` | 0 | 0 | 0 | none | Checks that the code runs at all. |
| `casual` | 25 ms | 20 ms | 1% | 50 ms every 10 s | A good home connection. |
| `realistic` | 60 ms | 50 ms | 3% | 100 ms every 10 s | The floor to playtest above. |
| `bad` | 150 ms | 100 ms | 5% | 200 ms every 5 s | Default. A friend across the continent on Wi-Fi. |
| `hostile` | 250 ms | 150 ms | 15% | 300 ms every 3 s | For checks: fails when the code is wrong. |

Pick one under **Project Settings > CrackNet > Autoconnect > Simulated Profile**. Leave it empty to use the custom fields
next to it (latency, packet loss chance, jitter, burst length and interval).

## Playtesting in the editor

1. **Project Settings > CrackNet > Autoconnect > Enabled** on, and choose a simulated profile.
2. **Debug > Customize Run Instances**: enable multiple instances and set the count.
3. Optionally **CrackNet > Extras > Auto Tile Windows**, to lay the windows out side by side.
4. Run `examples/playground/playground.tscn`. The first instance becomes the host, the others join, and each player gets
   a colour by joining order.

Autoconnect lands in `project.godot`. Do not commit it, and while it is on run headless checks with
`CRACKNET_NO_AUTOCONNECT=1`, or they connect to your editor instances.

Playground controls: WASD to move, Space to jump, F to grab and throw a crate, E to push a player in front of you, left
mouse or Enter to shoot. A crate is tinted with the colour of the peer simulating it right now.

## Reading the delay readout

The playground shows, per remote player, how far behind their state is displayed, split in two:

- **network** - how old a sample is when it arrives. Base latency plus jitter; nothing on the receiving side can
  reduce it.
- **playback** - how long it then waits in the buffer. It grows with that link's jitter and shrinks slowly after a bad
  spell.

Both are averaged over a second. On `clear` playback is about 2.5 ticks, one send interval plus a margin. If playback
is large on a link that looks clean, the buffer is wrong, not the network.

## The smoke check

Three headless processes form a mesh under a profile. Client A drives a crate, shoots a host crate and knocks a stack
over; the host and client B watch. Each fails with a reason. Positions are compared as displayed, per frame, against
the line between the samples the peer actually received, so loss is neither blamed on playback nor hidden by it.

```
<godot> --headless --path . res://examples/playground/playground.tscn -- --smoke --host --seconds=36 --port=20000 --profile=bad
<godot> --headless --path . res://examples/playground/playground.tscn -- --smoke --join --smoke-client=a --seconds=22 --port=20000 --profile=bad
<godot> --headless --path . res://examples/playground/playground.tscn -- --smoke --join --smoke-client=b --seconds=18 --port=20000 --profile=bad
```

Start the host first and give it a few seconds: a client that starts before the host is listening never connects.

## Writing checks that can fail

- **Measure what a player sees.** A check that compares state after everything has come to rest misses a crate
  drawn on the floor between two ticks, or a body left behind while its mesh moved. Sample displayed positions every
  frame during motion.
- **Make it fail first.** Reproduce the reported symptom with the check before fixing it, then break the fix on
  purpose and watch the check fail again. More than one check in this repository has passed by measuring nothing.
- **Count what the simulator did.** A configured burst that never fired reads exactly like a clean run.
