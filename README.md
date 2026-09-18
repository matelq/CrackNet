# CrackNet

Co-op netcode for Godot 4.7 .NET: distributed authority with state synchronization, after Glenn Fiedler's
[Networked Physics in Virtual Reality](https://gafferongames.com/post/networked_physics_in_virtual_reality/). Built for
"friend-slop" games - a handful of friends, shared physics objects, pushing each other, projectiles, joint QTEs - where
nobody cheats and everybody touches everything.

**3D only.** A `NetworkObject` under a 2D node (`Node2D`, `RigidBody2D`, `CharacterBody2D`) is an error. Non-spatial
roots (`Node`, `Control`) still replicate their `[Synced]` properties.

Started as a C# port of [netfox](https://github.com/foxssake/netfox). This branch has left rollback behind; the
`reworked` and `master` branches keep the port.

**[Guides](docs/README.md)** · **[Getting started](docs/getting-started.md)** · **[Minimal examples](docs/examples.md)** · **[Design and decisions](docs/design/distributed-authority.md)**

## The model in one paragraph

Every networked object has one peer that simulates it and sends its state - its authority - and everyone else plays
that state back a few ticks behind. Your own character is always yours, so input applies at once with no prediction
and no reconciliation. Touching a crate takes authority over it, and whatever it knocks over follows; grabbing takes
ownership, so nobody can snatch it back. The host arbitrates conflicting claims and gets objects back once they come
to rest. Pushes and hits are events delivered to whoever currently simulates the target.

## Status

Pre-release, under active playtesting. Verified in CI by core xUnit tests, Godot-side tests (several stacks in one
tree over a loopback peer) and a three-process ENet mesh smoke of the playground under a simulated bad network.

Not yet verified: **Steam against a live client** ([#6](https://github.com/matelq/CrackNet/issues/6)). The
playground can host and join over Steam; that path has been built and checked against the extension, not yet played.

## Using it

1. Copy `addons/cracknet` into `res://addons/` (a release zip carries the `CrackNet.Core` sources inside it).
2. Enable the plugin in **Project Settings > Plugins**. It registers the `cracknet/*` settings and the autoloads.
3. Enable `ImplicitUsings` and `Nullable`, and reference `addons/cracknet/analyzers/CrackNet.SourceGenerators.dll` as
   an `Analyzer` for `[Synced]`.
4. Assign `Multiplayer.MultiplayerPeer`. The clock starts on its own once the session does.
5. Add a `NetworkObject` under each replicated node.

A crate needs no code at all:

```
Crate (RigidBody3D)
├── CollisionShape3D
└── NetworkObject        Kind = Auto → Shared
```

Its transform and velocities are sent; it is frozen wherever another peer simulates it, passes authority to what it
hits, and goes back to the host at rest. A player needs only its own movement:

```csharp
public partial class Player : CharacterBody3D
{
    [Synced] public int Health { get; set; }        // game state; the transform is sent anyway

    public override void _PhysicsProcess(double delta)
    {
        if (!GetNode<NetworkObject>("NetworkObject").Authority.IsLocal) return;
        // read input, MoveAndSlide: crates walked into are taken automatically
    }
}
```

## Layout

| Path | What |
|---|---|
| `addons/cracknet/` | The addon: `NetworkObject`, the clock, transport helpers, the network simulator. |
| `CrackNet.Core/` | Engine-agnostic core: playback clock, sample tracks, clock sync math. |
| `CrackNet.SourceGenerators/` | `[Synced]` and its generator. |
| `docs/` | Guides, the design document, a generated API reference. |
| `examples/playground/` | The co-op sample: players, crates, grab and throw, pushes, projectiles, and the smoke check. |
| `examples/steam/` | GodotSteam bootstrap. |
| `test/`, `CrackNet.Core.Tests/` | Godot-side tests and xUnit tests. |

## Try the sample

From a fresh clone. You need [Godot 4.7.2 .NET](https://godotengine.org/download/archive/) - exactly
this version, older editors downgrade the SDK in `CrackNet.csproj` - the [.NET 10 SDK](https://dotnet.microsoft.com/download),
and for the script `sh`, `curl` and `python` (on Windows, Git Bash has the first two).

```
git clone https://github.com/matelq/CrackNet.git
cd CrackNet
sh tools/install-extensions.sh all      # Rapier (required: the crates are Rapier bodies) and GodotSteam
dotnet build CrackNet.csproj
godot --path . res://examples/playground/playground.tscn
```

Both extensions are native binaries and are not in the repository; the script pins their versions. `steam` alone
installs only GodotSteam, `rapier` only Rapier. Restart the editor after installing: extensions load at startup.

**On one machine:** press Host in one window and Join (`127.0.0.1`) in the others, or turn on autoconnect - see
[Testing on a real network](docs/real-networks.md). **Over LAN or with an open port:** Host, and the others Join the
host's address. The playground connects every pair directly, so over the internet each player needs UDP 9999 and
ports from 10200 upwards reachable - use Steam instead.

### Over Steam, with a friend

Each of you, on your own machine:

1. Do the fresh-clone steps above (`install-extensions.sh all` writes `steam_appid.txt` with 480, Valve's Spacewar test
   app, which is what the playground runs as).
2. Start the Steam client and log in. The two accounts have to be Steam friends: the lobby is friends-only.
3. Start the playground. The Steam buttons appear once GodotSteam has loaded; with the client not running, pressing
   them reports that in the status line.

Then:

- The host presses **Host on Steam**. The lobby id is copied to the clipboard and Steam's invite dialog opens.
- The friend, **with the playground already running**, accepts the invite or picks *Join Game* on the host in the
  friends list. With the game not running Steam would start Spacewar instead: that is app 480's, not ours.
- Or the host sends the lobby id, and the friend pastes it into the address field and presses **Join Steam lobby id
  above**.

Steam relays the traffic: no ports, no addresses. A simulated network profile (`-- --profile=casual|realistic|bad|hostile`,
or **Project Settings > CrackNet > Autoconnect > Simulated Profile**) is applied on top of the real network; without one
the Steam peer is used as it is. If Steam does not come up, `godot --headless --path . res://examples/steam/SteamSmoke.tscn`
says what is missing. Leave editor autoconnect off for this, or the instances connect to each other over ENet.
