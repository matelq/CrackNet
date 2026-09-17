# netfox-net

Co-op netcode for Godot C#: distributed authority with state synchronization. Started as a port of netfox; on this
branch (`distributed-authority`) it no longer is one. The model, its decisions and sources: `docs/design/distributed-authority.md`
- read it before changing anything networked. The rules of the port (1:1 with the `.gd` original, "Port of x.gd" summaries,
parity checks) do not apply here; `reworked` and `master` keep the port. Roadmap: the `Distributed authority` milestone.

## Layout

- `Netfox.Core/` — engine-agnostic core, no Godot reference (being merged into the addon). Godot aliases in `addons/netfox-net/Internal/GlobalUsings.cs`.
- `addons/netfox-net/` — the addon (namespace `Netfox`, extras in `Netfox.Extras`). Autoloads expose `Instance`; order is fixed in `Editor/NetfoxPlugin.cs` and `project.godot` (dependencies first).
- `test/` — Godot-side tests (`TestSuite` + `[Test]`), `Netfox.Core.Tests/` — xUnit. `test/Harness/` runs several stacks in one tree over a loopback peer.
- `NetfoxContext`: servers register into `NetfoxContext.Default`; a `NetfoxContextRoot` node gives its subtree a second stack. Nodes resolve `Context` in `_EnterTree`; `Instance` still points at the default stack.
- `examples/playground/` — the co-op sample and its two-process smoke; `examples/steam/` — GodotSteam bootstrap under `#if GODOTSTEAM`.
- Repo root is the Godot project. Godot 4.7.2 mono binary: `.tools/godot/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe` (gitignored). Use exactly this version: older editors downgrade the SDK in Netfox.csproj.
- **Rapier-first** (netfox-net#52): `project.godot` asks for `Rapier3D`, and the extension is gitignored, so a fresh clone
  needs `sh tools/install-extensions.sh rapier` before anything with a physics body runs (`steam` installs GodotSteam).
  The addon itself names no engine.

## Commands

```
dotnet test Netfox.slnx                                                   # core tests
dotnet build Netfox.csproj                                                # addon + tests
<godot> --headless --path . res://test/TestRunner.tscn                    # Godot tests, exit 0 = ok
<godot> --path . res://examples/playground/playground.tscn                # the sample: Host in one window, Join in others
<godot> --headless --path . res://examples/playground/playground.tscn -- --smoke --host --seconds=36 --port=20000
<godot> --headless --path . res://examples/playground/playground.tscn -- --smoke --join --smoke-client=a --seconds=22 --port=20000
<godot> --headless --path . res://examples/playground/playground.tscn -- --smoke --join --smoke-client=b --seconds=18 --port=20000   # same --profile=clear|casual|realistic|bad|hostile on all
```

Scenes are `.tscn` files and are the source of truth. To generate one in code, rebuild the tree and `ResourceSaver.Save`
it. Two traps: a node only lands in a packed scene when its `Owner` is the scene root, and Godot binds one Node-derived
class per `.cs` file, named after the file - a second class in the same file silently gets an empty embedded script.

## Property attributes

`Netfox.SourceGenerators` turns `[Synced]` on a property into the declaring interface `NetworkObject` gathers from, so
the paths come from the symbols and cannot go stale on a rename. The type has to be `partial` (NFX001 otherwise), and
properties only: Godot exposes a partial class's properties to `Get`/`Set`, not its plain fields. It is referenced as an
analyzer and ships built in the release zip under `addons/netfox-net/analyzers/`.

Rapier keeps a body's last kinematic target and returns to it on the next freeze: re-set the transform after
switching `Freeze` (`PhysicsHandling.SetFrozen`), or a crate handed back after a throw jumps to where it was held.

Two-process checks: give the host a longer head start than feels necessary. The Rapier GDExtension loads slower than
stock Godot, and a client that starts first simply never connects.

## Driving the editor and a running game (godot-mcp)

`addons/godot_mcp` (satelliteoflove/godot-mcp, MIT, committed) plus `.mcp.json` give the agent the editor: open and run
scenes, inject input actions, run GDScript inside the running game, screenshot it, and sample node fields per frame.
The plugin is enabled in project.godot and adds the `MCPGameBridge` autoload, which is inert headless (the suite and
the smokes run with it). It needs the editor open: `Godot_v4.7.2-stable_mono_win64.exe --editor --path .` in the
background; the addon then listens on 127.0.0.1:6550 and a freshly started Claude session gets the `godot_*` tools. In
a session that predates `.mcp.json`, talk to the server over stdio yourself (spawn `npx -y @satelliteoflove/godot-mcp`,
`initialize`, `tools/call`); it connects to the editor asynchronously, so wait a couple of seconds before the first call.
Quirks: `godot_exec` takes `source`, runs outside the tree, so reach the scene through
`Engine.get_main_loop().current_scene`; buttons are pressed with `emit_signal("pressed")`; `godot_input sequence` takes
`inputs: [{action_name, duration_ms, start_ms}]`; `screenshot_game` returns the PNG inline. `godot_game_time freeze` pauses the tree and holds the netfox tick, but
`step` makes NetworkTime catch up to the wall clock in a burst and other peers keep running - pause one peer to look at
it, never step a multiplayer session with it. Used first to see #60 with
your own eyes: the NPC had a collision shape and no mesh, and the after-screenshot is how the fix was checked.

## Docs

`docs/` holds the guides, written for C# users of the library rather than for this repo. `docs/api.md` is generated:
`dotnet build Netfox.csproj` then `python docs/generate-api.py`. Regenerate it when public API or its XML comments
change. Both projects have `GenerateDocumentationFile` on with CS1591 suppressed, so the XML exists without demanding
a comment on every member.

Editor playtests use autoconnect (Project Settings > Netfox > Autoconnect > Enabled, plus Debug > Customize Run
Instances). That setting lands in project.godot: never commit it, and run headless checks locally with
`NETFOX_NO_AUTOCONNECT=1` while it is on, or they connect to each other.

## Before committing

Warnings are errors in every project (`Directory.Build.props`). Fix the cause; do not suppress.

Run the whole CI set locally, not a subset: `dotnet format Netfox.slnx --verify-no-changes`, `dotnet test Netfox.slnx`,
`dotnet build Netfox.csproj`, `python tools/check-doc-examples.py`, then the Godot runner. `docs/examples.md` is how
the API is judged: keep every block marked and compiling. Building only the project you touched once let a broken
`Netfox.csproj` through, because the Godot project compiles everything under the repo root that is not excluded.

A check that only compares state after the keys are released is blind to what a player sees: a crate drawn on the
floor between two ticks, a remote player snapping, a body left behind while its mesh moved. Measure during motion and
on displayed positions (per frame, after interpolation) as well as at rest, and make the check fail on the reported
symptom before fixing it (netfox-net#59); the playground smoke compares what a peer displayed against the line
between the samples it actually received, so loss is not blamed on playback nor hidden by it. And when a check passes on
the first try, ask what it would take to make it fail; more than one check here has passed by measuring nothing
(netfox-net#62).

Performance claims come from tests that print their numbers (`PropertyAccessBenchmarkTests` and the
bandwidth case in the harness) and fail on regression. Tick-level timings swing by ±60% between runs on the
same build, so measure in isolation and take allocations and byte counts, which are stable, over wall clock.

## Releasing

Bump `version` in `addons/netfox-net/plugin.cfg`, then tag `vX.Y.Z`. The release workflow refuses a tag that disagrees
with plugin.cfg. It packages `addons/netfox-net` with the `Netfox.Core` sources copied into `addons/netfox-net/Core`, so
a consumer drops in one folder and needs no project reference, and it builds that layout on its own before zipping.
The addon needs `ImplicitUsings` and `Nullable` enabled in the consuming project.

## Conventions

- Signals → C# `event`. Events are NOT auto-disconnected when a node is freed: store the delegate and unsubscribe in `_ExitTree`.
- Command ids are explicit (`CommandIds`), never auto-incremented.
- Read settings via `Internal/Settings.cs`; log via `NetfoxLogger` with `{0}` placeholders.
- `Variant` has no value equality: use `VariantComparer`. `NodePath` is a valid dictionary key.
- Not on purpose: noray/nohub/trimsock, server authority, lag compensation by rewinding. Ask before adding them.

## Environment quirks

- Create C# files with the Write tool; the Bash heredoc wrapper here fails intermittently. Use Bash for builds and sed.
- `grep` output gets summarized after ~200 lines; read long files with `cat`/`sed`. `cd` in Bash persists.
