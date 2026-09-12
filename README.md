# netfox-net

Native C# port of [netfox](https://github.com/foxssake/netfox) (v1.48.5) for Godot 4.7 .NET: tick synchronization,
rollback with client-side prediction and server reconciliation, state synchronization, interpolation, and the
netfox.extras toolbox. No GDScript, no interop layer. The addon folder is `addons/netfox.cs`.

Status: functional and code parity with upstream, verified by 77 core tests, 115 Godot-side tests (including two
netfox stacks talking to each other in one process) and a two-process ENet run. Steam transport is wired but not yet tested against a live Steam client. Roadmap: [issues](https://github.com/matelq/netfox-net/issues).

## Layout

| Path | What |
|---|---|
| `addons/netfox.cs/` | The addon. Copy this folder into your project and reference `Netfox.Core`. |
| `Netfox.Core/` | Engine-agnostic core (history buffers, snapshots, serialization primitives, clock math). No Godot dependency. |
| `Netfox.Core.Tests/` | xUnit tests for the core: `dotnet test`. |
| `Netfox.Core.Benchmarks/` | BenchmarkDotNet cases for the core: history buffer, snapshot merge and diff, serializers, graph. |
| `test/` | Godot-side tests (servers, nodes, serializers, extras) with a tiny reflection-based runner. |
| `examples/e2e/` | Two-process ENet end-to-end scene. |
| `examples/steam/` | Steam lobby bootstrap on GodotSteam + C# bindings, compiled only with `GODOTSTEAM` defined. |
| `netfox/` | Local clone of the original GDScript addon, kept as the read-only reference. Not committed (excluded via `.git/info/exclude`); clone `foxssake/netfox` there yourself. |

The repository root is itself a Godot project (`project.godot`), like upstream netfox.

## Using it

1. Copy `addons/netfox.cs` into `res://addons/` and add a `ProjectReference` to `Netfox.Core.csproj` (or copy its sources).
2. Enable the plugin in Project Settings. It registers the `netfox/*` settings and the autoloads in dependency order.
3. Set `Multiplayer.MultiplayerPeer` (ENet, `SteamMultiplayerPeer`, anything). `NetworkEvents` starts `NetworkTime` on the host at once and on clients after `connected_to_server`.
4. Put a `RollbackSynchronizer` under your player, list state and input properties as `"Node:property"` paths, and implement `IRollbackTick` on the nodes to simulate.

Mapping from GDScript:

| GDScript | C# |
|---|---|
| `NetworkTime.on_tick.connect(f)` | `NetworkTime.Instance.OnTick += f` |
| `func _rollback_tick(delta, tick, is_fresh)` | `IRollbackTick.RollbackTick(double, int, bool)` |
| `_rollback_spawn / _rollback_despawn / _rollback_destroy` | `IRollbackSpawnAware / IRollbackDespawnAware / IRollbackDestroyAware` |
| `_get_rollback_state_properties()` etc. | `IRollbackStateProperties`, `IRollbackInputProperties`, `ISynchronizedStateProperties`, `IInterpolatedProperties` |
| `BaseNetInput._gather()` | `BaseNetInput.Gather()` (protected virtual) |
| `NetworkSchemas.vec3f32()` | `NetworkSchemas.Vec3F32()` |
| Physics driver `.off` file toggles | Add `RapierPhysicsDriver3D` / `GodotPhysicsDriver3D` (2D variants too) to the scene; availability is checked at runtime |

Signals are C# events. Autoloads keep their names and expose `Instance`. Command ids are fixed (`CommandIds`) instead of
depending on autoload order. Snapshot sanitization actually drops foreign subjects (upstream `snapshot.gd:116` erased nothing).

## Tests

```
dotnet test Netfox.slnx                                  # 77 core tests
dotnet run -c Release --project Netfox.Core.Benchmarks -- --filter '*'   # core benchmarks
dotnet build Netfox.csproj
godot --headless --path . res://test/TestRunner.tscn     # 115 Godot-side tests, exit code 0 on success
```

End to end over ENet, two processes:

```
godot --headless --path . res://examples/e2e/E2E.tscn -- --host --seconds=14
godot --headless --path . res://examples/e2e/E2E.tscn -- --join --seconds=10
```

Each prints an `E2E RESULT ... ok=True` line. The host keeps a trace of the position it simulated for every tick, and the
client checks the state it received against it (`compared_ticks`, `max_error=0`), so a run proves replication is exact, not
only that packets arrived. Add `--latency=40 --loss=3` to both to route the run through the NetworkSimulator proxy.

## Steam

`examples/steam/SteamLobbyBootstrap.cs` shows host/join through GodotSteam. Install the GodotSteam GDExtension (4.22+)
and `GodotSteam_CSharpBindings`, define `GODOTSTEAM` in your csproj, add the node to your lobby scene and call `Host()` or `Join(lobbyId)`.
The library itself is transport-agnostic; a pure C# `MultiplayerPeerExtension` over Facepunch.Steamworks would slot in the same way.

## License

MIT, see [LICENSE](LICENSE). Derived from netfox by Gálffy Tamás (Fox and Sake), also MIT; the original notice is kept in
`addons/netfox.cs/LICENSE.netfox`.
