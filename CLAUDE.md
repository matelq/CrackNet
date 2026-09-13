# netfox-net

Native C# port of netfox (GDScript rollback netcode for Godot). Reference GDScript lives in `netfox/` (read-only, `.gdignore`,
gitignored; clone foxssake/netfox there if missing). Roadmap lives in GitHub issues.
Rule: 1:1 semantics and public API with the original, idiomatic C# inside. When in doubt, read the matching `.gd` file first.
Branches: `reworked` is the default and where work happens; `master` keeps parity with the original, so changes that deviate from its design (NetfoxContext, settings object, shared synchronizer base) never land there.

## Layout

- `Netfox.Core/` — engine-agnostic core, no Godot reference. Generic over `TSubject, TProperty, TValue`; Godot aliases in `addons/netfox-net/Internal/GlobalUsings.cs`.
- `addons/netfox-net/` — the addon (namespace `Netfox`, extras in `Netfox.Extras`). Autoloads expose `Instance`; order is fixed in `Editor/NetfoxPlugin.cs` and `project.godot` (dependencies first).
- `test/` — Godot-side tests (`TestSuite` + `[Test]`), `Netfox.Core.Tests/` — xUnit. `test/Harness/` runs two stacks in one tree over a loopback peer (reworked).
- `NetfoxContext` (reworked only): servers register into `NetfoxContext.Default`; a `NetfoxContextRoot` node gives its subtree a second stack. Nodes resolve `Context` in `_EnterTree`; `Instance` still points at the default stack.
- `examples/e2e/` — two-process ENet check; `examples/steam/` — GodotSteam bootstrap under `#if GODOTSTEAM`;
  `examples/playground/` — the scene-based sample (see its README), driven headless by `PlaygroundSmoke.tscn` in CI;
  `examples/parity/` and `examples/physics/` — the checks below.
- Repo root is the Godot project. Godot 4.7.2 mono binary: `.tools/godot/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe` (gitignored). Use exactly this version: older editors downgrade the SDK in Netfox.csproj.
- **The project is Rapier-first** (netfox-net#52): `project.godot` asks for `Rapier3D`, and the extension is gitignored, so a fresh clone
  needs `sh tools/install-extensions.sh rapier` before anything with a physics body runs. The addon itself names no engine;
  CI runs the suite on stock Godot Physics too (`godot-stock` job), with `project.godot` switched back by `sed`.

## Commands

```
dotnet test Netfox.slnx                                                   # core tests
dotnet run -c Release --project Netfox.Core.Benchmarks -- --filter '*'    # core benchmarks (BenchmarkDotNet)
dotnet build Netfox.csproj                                                # addon + tests
<godot> --headless --path . res://test/TestRunner.tscn                    # Godot tests, exit 0 = ok
<godot> --headless --path . res://examples/e2e/E2E.tscn -- --host --seconds=14   # host must outlive the client
<godot> --headless --path . res://examples/e2e/E2E.tscn -- --join --seconds=10  # add --profile=realistic on both for the proxy run
<godot> --headless --path . res://examples/playground/PlaygroundSmoke.tscn -- --host --seconds=14   # sample, host first
<godot> --headless --path . res://examples/playground/PlaygroundSmoke.tscn -- --join --seconds=8
<godot> --headless --path . res://examples/playground/PlatformRideCheck.tscn   # rider stays on the moving platform
<godot> --headless --path . res://examples/playground/ConvergenceSmoke.tscn -- --host|--join   # peers agree after colliding; add --peers=3 (one process each), --on-platform, --profile=realistic|hostile
<godot> --headless --path . res://examples/playground/PlatformPushCheck.tscn [-- --no-push]   # #41 reproducer, fails on purpose; --no-push is the control
```

The playground scenes are `.tscn` files and are the source of truth. They were generated once by a throwaway script
rather than typed by hand; to change them, edit in the editor, or rebuild the tree in code and `ResourceSaver.Save` it.
Two traps if you do: a node only lands in a packed scene when its `Owner` is the scene root, and Godot binds one
Node-derived class per `.cs` file, named after the file - a second class in the same file silently gets an empty
embedded script.

## Property attributes

`Netfox.SourceGenerators` turns `[RollbackState]` / `[RollbackInput]` / `[SynchronizedState]` / `[Interpolated]` on a
property into the declaring interface the synchronizers gather from, so the paths come from the symbols and cannot go
stale on a rename. The type has to be `partial` (NFX001 otherwise), and properties only: Godot exposes a partial
class's properties to `Get`/`Set`, not its plain fields. It is referenced as an analyzer, ships built in the release
zip under `addons/netfox-net/analyzers/`, and is optional - the interfaces can still be implemented by hand.

## Physics drivers

`examples/physics/RapierCheck.tscn` rolls a rigid body back through the Rapier driver; without the extension it skips
and exits 0, and `-- --require` turns that skip into a failure, which is how CI runs it. `sh tools/install-extensions.sh
rapier` unpacks the pinned release into `addons/godot-rapier3d` (gitignored); `--enable-rapier` also writes
`3d/physics_engine="Rapier3D"` under `[physics]` in project.godot, which is already committed - note that project.godot
strips the section name from its keys. The same script installs GodotSteam with `steam`. Stock Godot has no
`space_step`, so `GodotPhysicsDriver2D/3D` report themselves unavailable and only work on a build carrying
godotengine/godot PR 76462. Rolling a `RigidBody` back means re-stepping the physics space several times inside one
frame, which is why the driver exists at all; a kinematic body needs none of it. Two Rapier facts the driver encodes
(netfox-net#62): a whole-space rewind moves kinematic bodies too, so the driver pushes every kinematic node's transform
to its body on each resimulated tick and flushes the space once, because Rapier applies transforms on the next step
and `MoveAndSlide` is a query. Never flush inside the tick after one body moves: the next body then sees it, and the
order bodies move in differs between peers (14cm permanent disagreement, seen in CI). A held
pickup is out of the world (no collision layer, frozen) and drawn from its holder every frame after interpolation,
never moved inside the tick (netfox-net#59).

Two-process checks: give the host a longer head start than feels necessary. The GDExtension loads slower than stock
Godot, and a client that starts first simply never connects - `ConvergenceSmoke` then reports `players=0`, which reads
like a broken check rather than a slow start.

## Docs

`docs/` holds the guides, written for C# users of the library rather than for this repo. `docs/api.md` is generated:
`dotnet build Netfox.csproj` then `python docs/generate-api.py`. Regenerate it when public API or its XML comments
change. Both projects have `GenerateDocumentationFile` on with CS1591 suppressed, so the XML exists without demanding
a comment on every member.

## Parity against the original

`sh parity/run-parity.sh` runs the same scene against the GDScript original in `netfox/` and against this port, and
compares the tick traces. One offline peer on both sides, so nothing depends on packet timing and the comparison is
exact: same ticks simulated, same state per tick, same number of simulations per tick. CI clones the original at a
pinned commit and runs it. Keep `parity/parity.gd` and `examples/parity/Parity.cs` identical, arithmetic included.

## Before committing

Run the whole CI set locally, not a subset: `dotnet format Netfox.slnx --verify-no-changes`, `dotnet test Netfox.slnx`,
`dotnet build Netfox.csproj`, then the Godot runner. Building only the project you touched once let a broken
`Netfox.csproj` through, because the Godot project compiles everything under the repo root that is not excluded.

A check that only compares state after the keys are released is blind to what a player sees: a crate drawn on the
floor between two ticks, a remote player snapping, a body left behind while its mesh moved. Measure during motion and
on displayed positions (per frame, after interpolation) as well as at rest, and make the check fail on the reported
symptom before fixing it - `ConvergenceSmoke --pickup` shows the pattern (netfox-net#59). And when a check passes on
the first try, ask what it would take to make it fail; more than one check here has passed by measuring nothing
(netfox-net#62).

Performance claims come from tests that print their numbers (`PropertyAccessBenchmarkTests`, `HotPathBenchmarkTests`,
and the bandwidth case in the harness) and fail on regression. Tick-level timings swing by ±60% between runs on the
same build, so measure in isolation and take allocations and byte counts, which are stable, over wall clock.

## Releasing

Bump `version` in `addons/netfox-net/plugin.cfg`, then tag `vX.Y.Z`. The release workflow refuses a tag that disagrees
with plugin.cfg. It packages `addons/netfox-net` with the `Netfox.Core` sources copied into `addons/netfox-net/Core`, so
a consumer drops in one folder and needs no project reference, and it builds that layout on its own before zipping.
The addon needs `ImplicitUsings` and `Nullable` enabled in the consuming project.

## Conventions

- Signals → C# `event`. Events are NOT auto-disconnected when a node is freed: store the delegate and unsubscribe in `_ExitTree`. For the three synchronizers that lives in `BaseSynchronizer` (reworked).
- Duck-typing (`has_method("_rollback_tick")`) → interfaces in `Rollback/RollbackInterfaces.cs`.
- Command ids are explicit (`CommandIds`), never auto-incremented.
- Read settings via `Internal/Settings.cs`; log via `NetfoxLogger` with `{0}` placeholders.
- `Variant` has no value equality: use `VariantComparer` / `Snapshot.ValueComparer`. `NodePath` is a valid dictionary key.
- Every ported class states its source in the summary (`Port of servers/x.gd`). Keep that.
- Not ported on purpose: noray/nohub/trimsock, `.off` driver file toggles. Do not add lag compensation, typed struct snapshots or a Facepunch peer without asking; they are listed as TODO in the plan.

## Environment quirks

- Create C# files with the Write tool; the Bash heredoc wrapper here fails intermittently. Use Bash for builds and sed.
- `grep` output gets summarized after ~200 lines; read long files with `cat`/`sed`. `cd` in Bash persists.
