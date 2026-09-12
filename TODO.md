# TODO

Current state: full functional and code parity with netfox 1.48.5, including netfox.extras and the Godot/Rapier physics drivers.
77 core tests, 90 Godot-side tests, two-process ENet e2e pass. Not yet exercised against a live Steam client.

## Documentation

- [ ] Port the netfox guides that matter for C# users: NetworkTime, NetworkRollback, RollbackSynchronizer, prediction, rollback caveats, spawning/despawning, schemas, visibility.
- [ ] API reference from XML doc comments (DocFX or a plain generated Markdown pass).
- [ ] Getting-started tutorial: `examples/e2e` as a walkthrough, then a Steam walkthrough once verified.
- [ ] Migration notes GDScript → C# (expand the table in README: method names, interfaces, events, `Instance`).
- [ ] Document the deliberate deviations: explicit `CommandIds`, working `Sanitize`, C# events, no `.off` driver toggles.

## Tests

- [ ] Remaining vest cases not ported: `RollbackSynchronizer` input-age and last-known-input/state (todo() upstream too), `one-off-input` snippet, `.perf.gd` benchmarks as BenchmarkDotNet.
- [ ] In-process two-peer harness: needs the servers to stop being process-wide singletons (see Refactoring), then loopback `MultiplayerPeerExtension`.
- [ ] Reconciliation assertion in e2e: compare client-predicted vs host-authoritative position at the same tick, not only "data flowed".
- [ ] Packet-loss and latency e2e using `NetworkSimulator` (proxy path is ported but untested).
- [ ] Parity trace: run `netfox/examples/multiplayer-simple` in GDScript and the C# e2e with identical settings, diff `NetfoxLogger` tick logs.
- [ ] Steam smoke test with two Steam accounts once GodotSteam bindings are installed.

## CI

- [ ] GitHub Actions: `dotnet test Netfox.slnx` on push/PR.
- [ ] Download Godot 4.7 mono headless in CI, `dotnet build Netfox.csproj`, run `res://test/TestRunner.tscn`, fail on non-zero exit.
- [ ] Two-process e2e job (host in background, client foreground, grep `ok=True`).
- [ ] `dotnet format --verify-no-changes` against `.editorconfig`.
- [ ] Release job: zip `addons/netfox.cs` + `Netfox.Core` sources, attach to tag.

## Features

- [ ] Pure C# `SteamMultiplayerPeer` over Facepunch.Steamworks (`MultiplayerPeerExtension`), so the GDExtension and beta bindings become optional. Reference: Pieeer1/SteamMultiplayerPeer.
- [ ] Typed snapshots: `IRollbackNode<TState, TInput>` with `unmanaged struct` state, `MemoryMarshal` on the wire. Alternative path next to string property paths, not a replacement.
- [ ] Dedicated server via Steam Game Server API.
- [ ] Rapier driver verification against a real godot-rapier install (2D and 3D); Box3D driver once its Godot integration exposes stepping and snapshots.
- [ ] `NetworkSimulator` for non-ENet peers (currently hardcoded to ENet like upstream).
- [ ] Redundant input packets as first-full-then-diffs (upstream TODO #560).
- [ ] Graceful handling of unknown identity references instead of dropping the frame (upstream TODO #563).

## Optimizations

- [ ] Cache `GetIndexed`/`SetIndexed` accessors per (Type, NodePath): compiled getter/setter delegates for plain properties, fall back to `GetIndexed` only for sub-property paths like `position:x`.
- [ ] Cache `IsMultiplayerAuthority()` per subject per tick; it is called on every record/restore.
- [ ] Avoid `Variant` boxing in `Snapshot` for value types; consider a typed `Variant`-free fast path once typed snapshots exist.
- [ ] Pool `ByteWriter`/`ByteReader` and packet buffers in the synchronization server hot path.
- [ ] `HistoryBuffer<T>.Values()` allocates a list per call; `NetworkHistoryServer.Deregister` iterates all snapshots. Both are fine today, revisit under profiling.
- [ ] `RollbackSimulationServer.GetNodesToSimulate` walks the whole scene group each tick; keep a sorted cache invalidated on register/deregister.
- [ ] Measure Godot C# virtual-call overhead on `_Process` for 12 autoloads; consider one driver node ticking the rest.

## Refactoring

- [ ] Replace `Instance` singletons with a `NetfoxContext` owning all servers, so two contexts can live in one process (enables in-process e2e and dedicated-server-in-editor).
- [ ] Merge the three synchronizers' shared code (managed-node discovery, schema handling, reprocess-on-connect, event cleanup) into one base class.
- [ ] Move `EnableInputBroadcast` and other runtime toggles from settings-at-construction to a mutable `NetfoxSettings` object read once, so tests do not depend on `project.godot`.
- [ ] `NetworkSchemas`: drop the `Godot.Variant` name clash by renaming the factory methods to `AnyVariant`/`Text`, or keep parity and accept the qualification. Decide once docs are written.
- [ ] `PhysicsDriver` snapshots dictionary is `object`-typed to share between Godot and Rapier drivers; give it a proper generic base.
- [ ] Remove the `[Obsolete]` netfox compatibility properties on `NetworkTime` (RemoteTick, LocalTime, ...) in the first major version.
- [ ] Consider a source generator for `IRollbackStateProperties` so property lists are checked at compile time.
