# netfox.cs

Native C# port of [netfox](https://github.com/foxssake/netfox) for Godot 4.7 .NET: tick synchronization, rollback with
client-side prediction and server reconciliation, state synchronization, interpolation, and the netfox.extras toolbox.
No GDScript, no interop layer.

Same semantics and the same public API as the original — checked tick by tick against it, not just by eye. What differs
is that you write C#: interfaces instead of duck-typed methods, events instead of signals, attributes instead of
property-name strings.

**[Guides](docs/README.md)** · **[Getting started](docs/getting-started.md)** · **[Coming from GDScript](docs/migration.md)** · **[Sample](examples/playground/README.md)**

## Status

Functional and API parity with upstream, at `v0.1.0` (pre-release). Verified by 77 core tests, 156 Godot-side tests —
including two netfox stacks talking to each other inside one process — a two-process ENet run with and without a
latency and loss proxy, a headless run of the sample game, and a tick-by-tick trace compared against the GDScript
original. All of it runs in CI.

Not yet verified: **Steam against a live client.** The transport is written and compiles, but it has never talked to
Steam. See [#6](https://github.com/matelq/netfox-net/issues/6).

Roadmap lives in [issues](https://github.com/matelq/netfox-net/issues).

## Using it

1. Take `addons/netfox.cs` from the [latest release](https://github.com/matelq/netfox-net/releases) and drop it into
   `res://addons/`. The zip already carries the `Netfox.Core` sources inside it, so there is no project reference to
   add.
2. Enable the plugin in **Project Settings > Plugins**. It registers the `netfox/*` settings and the autoloads in
   dependency order.
3. Your project needs `ImplicitUsings` and `Nullable` enabled. Optionally reference
   `addons/netfox.cs/analyzers/Netfox.SourceGenerators.dll` as an `Analyzer` for the property attributes.
4. Assign `Multiplayer.MultiplayerPeer` — ENet, Steam, anything. `NetworkEvents` starts `NetworkTime` on the host at
   once and on clients once they are connected.
5. Put a `RollbackSynchronizer` under your player and implement `IRollbackTick` on what should simulate.

[Getting started](docs/getting-started.md) walks through all of it with code.

```csharp
[GlobalClass]
public partial class Player : CharacterBody3D, IRollbackTick
{
    [RollbackState] public int JumpsLeft { get; set; }

    public void RollbackTick(double delta, int tick, bool isFresh)
    {
        // A function of the state you were given and the input for this tick, and nothing else
    }
}
```

## Layout

| Path | What |
|---|---|
| `addons/netfox.cs/` | The addon. This is what you copy into a project. |
| `Netfox.Core/` | Engine-agnostic core: history buffers, snapshots, serialization, clock math. No Godot dependency. |
| `Netfox.SourceGenerators/` | The `[RollbackState]` attributes and the generator behind them. Optional. |
| `docs/` | The guides, and a generated API reference. |
| `examples/playground/` | A playable sample built out of scenes: lobby, players, prediction, weapon, physics. |
| `examples/e2e/`, `examples/parity/`, `examples/physics/` | The end-to-end, parity and physics checks. |
| `test/`, `Netfox.Core.Tests/`, `Netfox.Core.Benchmarks/` | Godot-side tests, xUnit tests, benchmarks. |
| `parity/` | The GDScript side of the parity trace, and the script that compares the two. |

The repository root is itself a Godot project, like upstream netfox.

## Try the sample

```
godot --path . res://examples/playground/playground.tscn
```

Two instances, Host in one and Join in the other. It shows movement through a rewindable state machine, prediction,
interpolation, a weapon on the request-and-accept model, replication without rollback, and visibility filtering — and
lights up rigid body rollback and Steam hosting when those are installed.

**[TESTING.md](examples/playground/TESTING.md)** walks through it from nothing - installing Godot, running two
instances, adding latency until things visibly break, and reading what the status line is telling you. Its
[README](examples/playground/README.md) points at the parts of the code worth reading.

## Checks

```
dotnet test Netfox.slnx                                   # 77 core tests
dotnet build Netfox.csproj
godot --headless --path . res://test/TestRunner.tscn       # 156 Godot-side tests, exit 0 on success
sh parity/run-parity.sh                                   # tick traces against the GDScript original
godot --headless --path . res://examples/playground/PlatformRideCheck.tscn   # rider stays on the moving platform
```

End to end over ENet, two processes — the host has to outlive the client:

```
godot --headless --path . res://examples/e2e/E2E.tscn -- --host --seconds=14
godot --headless --path . res://examples/e2e/E2E.tscn -- --join --seconds=10
```

Each prints an `E2E RESULT ... ok=True` line. The host keeps a trace of the position it simulated for every tick and
the client checks what it received against it, so a run proves replication is exact rather than merely that packets
arrived. Add `--latency=40 --loss=3` to both to route it through the latency and loss proxy.

## Steam

The transport is the [GodotSteam](https://codeberg.org/godotsteam/godotsteam) GDExtension, driven from C# through
`ClassDB` — no C# bindings involved, the same way the Rapier physics drivers work. Run
`sh tools/install-extensions.sh steam` to install it, then use `examples/steam/SteamLobbyBootstrap.cs` to create or
join a lobby. `examples/steam/SteamSmoke.tscn` reports whether everything it needs is present.

netfox itself is transport-agnostic: once a peer is assigned, nothing above it knows the difference.

## Differences from the original

Beyond the C# shape of the API, this port fixes several upstream bugs and deviates from a few upstream decisions on
purpose — a working `Sanitize`, state sent once per loop rather than once per resimulated tick, servers reset between
sessions, input that reaches the peers that need it. Each one, with its reasoning, is in the
[migration notes](docs/migration.md).

There is no wire compatibility with GDScript netfox, and none is intended.

## License

MIT, see [LICENSE](LICENSE). Derived from netfox by Gálffy Tamás (Fox and Sake), also MIT; the original notice is kept
in `addons/netfox.cs/LICENSE.netfox`.
