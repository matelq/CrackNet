# Testing the playground, from scratch

A walkthrough for someone who has not touched this repository before. By the end you will have two players running on
one machine, seen rollback correct a wrong guess in front of you, and know how to tell working netcode from broken
netcode.

No prior netfox knowledge needed. If you want to understand *why* any of it works afterwards, start with the
[guides](../../docs/README.md).

---

## 1. What you need

**Godot 4.7.2, the .NET build.** Not the standard build - the project is C#, and the standard build cannot run it.
Download it from [godotengine.org/download](https://godotengine.org/download) (pick ".NET") or from the
[godot-builds releases](https://github.com/godotengine/godot-builds/releases/tag/4.7.2-stable).

Use exactly 4.7.2. An older editor silently downgrades the SDK version in `Netfox.csproj`, and then nothing builds.

**The .NET 10 SDK.** Check with `dotnet --version`; you want 10.x. Get it from
[dot.net](https://dotnet.microsoft.com/download).

Nothing else. Steam and the physics extension are optional and come later.

## 2. First run

```
dotnet build Netfox.csproj
```

That has to succeed before Godot can open the project - Godot runs the compiled assembly, it does not compile on
demand in a useful way here.

Then open the project in Godot (point it at the repository root, which is itself a Godot project) and run:

```
res://examples/playground/playground.tscn
```

Or straight from the command line, without opening the editor:

```
<godot> --path . res://examples/playground/playground.tscn
```

You should get a grey floor, a moving platform, a small sphere circling, and a panel with **Host**, **Join** and
**Host on Steam**. "Host on Steam" being greyed out is correct - that tier is off until you install GodotSteam.

Press **Host**. A capsule falls in and you can move it: arrows or WASD, Space to jump (twice - it has a double jump),
Enter to shoot.

**If nothing happens when you press Host**, look at the status line along the top. It says what went wrong.

## 3. Two players, which is the point

One instance proves nothing about networking. You need two.

**In the editor:** *Debug > Customize Run Instances*, tick **Enable Multiple Instances**, set the count to 2, and run.
Two windows appear.

**From the command line:** run the same command twice in two terminals.

In the first window press **Host**. In the second, leave the address as `127.0.0.1` and press **Join**.

You now have two capsules. Each window drives its own. Move one and watch it move in the other window too.

### What to look at

The status line, in both windows:

```
peer #1  tick 412  players 2  shots 0  rollback 408>412  physics kinematic (...)  props 6/24
```

| Field | Meaning |
|---|---|
| `peer` | This instance's id. The host is always 1. |
| `tick` | The network tick. **Both windows should show nearly the same number** - that is clock synchronization working. A gap of a few is fine; a gap that grows is not. |
| `players` | How many are in the session. |
| `shots` | The scoreboard, decided by the host and replicated to everyone. It should match in both windows. |
| `rollback a>b` | The range the last resimulation covered. On a local connection this is usually one tick. Under latency it gets wider - that is rollback doing its job. |
| `physics` | Which physics tier is active. |
| `props sent/full` | Properties actually sent against properties that exist. The gap is what diff states save you. |

## 4. Making it misbehave on purpose

On a local connection everything is instant and everything looks perfect, which teaches you nothing. Add latency.

*Project > Project Settings*, turn on **Advanced Settings**, then **Netfox > Autoconnect**:

| Setting | Set to |
|---|---|
| `Enabled` | on |
| `Simulated Latency Ms` | `80` |
| `Simulated Packet Loss Chance` | `0.05` |

With autoconnect on, the instances connect to each other by themselves - the first to start hosts, the rest join, and
you can ignore the lobby panel entirely. Traffic goes through a local UDP proxy that delays and drops packets.

Run two instances again and watch:

- **Your own player still moves instantly.** That is client-side prediction: you do not wait for the host to agree.
- **The other player moves a little behind, and occasionally jumps to a corrected position.** That is reconciliation -
  the host's answer arriving and overriding the guess.
- **`rollback a>b` in the status line now spans several ticks**, because late input keeps forcing resimulation.
- **Stand on the moving platform.** It carries you, and keeps carrying you at any latency. The platform computes its
  position from the tick number and the player is carried by that same per-tick motion, so a resimulated tick puts
  both exactly where the first pass did. Godot's own moving platform support is switched off here on purpose: it
  works per physics frame, and a rollback runs many ticks inside one frame, which slides the rider off the end. See
  [rollback caveats](../../docs/rollback-caveats.md).
- **Walk towards the small sphere circling near the far edge.** On a client it is invisible until you get within 10
  metres, then it appears and circles; walk away and it vanishes again. That is the visibility filter: beyond that
  distance the host never sends its position, so the client does not have the data at all rather than having it and
  hiding it. On the host it is always visible, because the host is the one simulating it.

Push the latency to 300ms and the loss to 0.3 and it gets ugly in an instructive way. That is what the knobs are for.

## 5. Checking it without a window

The same scene runs headless and reports whether it worked. The host has to outlive the client:

```
<godot> --headless --path . res://examples/playground/PlaygroundSmoke.tscn -- --host --seconds=14
```

and, within the next few seconds, in another terminal:

```
<godot> --headless --path . res://examples/playground/PlaygroundSmoke.tscn -- --join --seconds=8
```

Each prints one line and exits 0 on success:

```
PLAYGROUND role=client ok=True peer=#1876241018 tick=413 synced=True players=[Player_1876241018,Player_1]
  peak=2 physics=godot-rapier3d not installed crates=0 state=Grounded shots=1/0 score=2
  platform_x=5.82 own_pos=(9.5, 0.90, 1) jumps=2
```

`ok=True` means all of this held: the clock synchronized, both players were seen, the player moved under its own
input, it ended up standing **on** the ground rather than having fallen through it, the state machine transitioned
from `Airborne` to `Grounded`, a shot was accepted, the scoreboard reached this peer, and the platform moved.

There is a second, single-process check for the one thing two windows make hard to eyeball - whether a player
standing on the moving platform is carried by it:

```
<godot> --headless --path . res://examples/playground/PlatformRideCheck.tscn
```

It drops a player on the platform, runs 150 ticks and a forced resimulation, and fails if the gap between the two
drifts by more than 5 cm.

Both are what CI runs on every commit, so if either fails on a clean checkout, something is genuinely broken.

## 6. Optional: rolling back real physics

You do not need this for anything above. The platform, the players and the projectiles are all kinematic, which stock
Godot handles. Rapier is only for the crates - actual rigid bodies.

`sh tools/install-extensions.sh` installs both in one go, if you want them without reading the rest of this.

Neither Rapier nor GodotSteam ships with this repository, and that is deliberate rather than an omission: both are
native GDExtensions, which means a separate binary per platform, tens of megabytes, their own licences, and a build
tied to a particular Godot version. Bundling them would make the release zip platform specific and force everyone
onto one Godot and one Steam SDK. They are installed per project, which is how GDExtensions are normally shipped.

Stock Godot cannot rewind rigid bodies at all - there is no way to step its physics by hand, so rollback cannot
resimulate it. Rapier can.

```
sh tools/install-extensions.sh rapier --enable-rapier
```

That downloads a pinned release, unpacks it into `addons/godot-rapier3d/`, and sets the physics engine. Restart
Godot afterwards - an extension is only loaded at startup. By hand instead, if you would rather:

1. Download `godot-rapier-3d-single.zip` from
   [godot-rapier-physics releases](https://github.com/appsinacup/godot-rapier-physics/releases).
2. Unzip so that you end up with `addons/godot-rapier3d/` in this repository.
3. *Project Settings > Advanced > Physics > 3D*, set **Physics Engine** to `Rapier3D`.
4. Restart Godot, and run the playground again.

The status line now says `physics rapier`, and three crates appear that you can shove around. They are rolled back
and resimulated like everything else.

There is no determinism option to hunt for: Rapier ships one build per dimension and it is already cross-platform
deterministic.

A caveat worth knowing: a rewind steps the *entire physics space* again for every tick of its range, so this is far
more expensive than kinematic rollback. With high latency and many bodies you will feel it. The sample uses one
physics step per tick rather than the default two for this reason.

To go back, delete `addons/godot-rapier3d` and set the physics engine to `DEFAULT`.

## 7. Optional: hosting through Steam

Install and log into the Steam client, then:

```
sh tools/install-extensions.sh steam
```

That unpacks the extension into `addons/godotsteam/` and writes the `steam_appid.txt` it needs. Restart Godot
afterwards. By hand instead:

1. Download the GDExtension from [GodotSteam releases](https://codeberg.org/godotsteam/godotsteam/releases) - the
   asset named `...-gdextension-plugin-...zip`, from a `-gde` tag.
2. Unzip so you have `addons/godotsteam/`.
3. Put a file named `steam_appid.txt` containing `480` next to the Godot executable and in the project directory.
   480 is Valve's Spacewar, the app id everyone develops against before they have their own.
4. Restart Godot.

Check it took:

```
<godot> --headless --path . res://examples/steam/SteamSmoke.tscn
```

It prints whether the extension loaded, whether the classes and methods it needs exist, and whether Steam initialised.
Without a running Steam client the last part fails, and that is expected.

Then run the playground: **Host on Steam** is now enabled. Press it, and the status line shows a lobby id to share.

**This path has not been verified against a live Steam client yet** - see
[#6](https://github.com/matelq/netfox-net/issues/6). If you get it working, or it breaks, that issue is the place to
say so.

## 8. When something looks wrong

**The two windows disagree about where a player is, and it never settles.** Something in a rollback tick is not a
function of state and input. The usual culprits are in [rollback caveats](../../docs/rollback-caveats.md): a value
that should be rollback state and is not, a frame timer, an un-rewound random number.

**The player falls through the floor.** `IsOnFloor` only updates during `MoveAndSlide`, and a rewind restores the
position but not the flag. The sample calls `RefreshIsOnFloor` for this.

**A shot gets rejected** and the console says `Cannot reconcile states`. The shooter and the host disagreed about
where the shot came from by more than `DistanceThreshold`. That is the anti-cheat check doing its job; if it fires
constantly, the tolerance is too tight for how fast players move and how much latency there is.

**Ticks drift apart between windows.** Clock synchronization is not keeping up. Check that both are running the same
tickrate - the host's wins, and a mismatch warns in the console.

**The sphere near the far edge never moves on a client.** It is out of range of the visibility filter, so the host
is not sending its position. Walk within 10 metres and it appears. Since the whole point is that the client has no
data, it is hidden rather than left frozen - if you see it frozen instead, `Beacon._Process` is not running.

**Nothing replicates at all.** Check the node names match on both peers. netfox addresses nodes by their path, so a
player the host calls `Player_2` has to be `Player_2` everywhere.

The console is worth reading. netfox logs each rollback with a tag like `R@59|58>122` - stage, tick being
resimulated, and the range - which usually tells you what it was doing when things went wrong.
