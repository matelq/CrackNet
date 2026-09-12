# Coming from GDScript netfox

The semantics are the same. This port is checked tick by tick against the original - same scene, same inputs, same
settings, run against both, traces compared exactly (`parity/`). What changes is how you write against it.

## The translation table

| Original | Here |
|---|---|
| `NetworkTime.tick` | `NetworkTime.Instance.Tick` |
| `NetworkTime.before_tick.connect(f)` | `NetworkTime.Instance.BeforeTick += f` |
| `_rollback_tick(delta, tick, is_fresh)` | `IRollbackTick.RollbackTick(double delta, int tick, bool isFresh)` |
| `_rollback_spawn()` / `_rollback_despawn()` / `_rollback_destroy()` | `IRollbackSpawnAware` / `IRollbackDespawnAware` / `IRollbackDestroyAware` |
| `_get_rollback_state_properties()` | `IRollbackStateProperties`, or `[RollbackState]` on the property |
| `_get_rollback_input_properties()` | `IRollbackInputProperties`, or `[RollbackInput]` |
| `_get_synchronized_state_properties()` | `ISynchronizedStateProperties`, or `[SynchronizedState]` |
| `_get_interpolated_properties()` | `IInterpolatedProperties`, or `[Interpolated]` |
| `_gather()` on `BaseNetInput` | `protected override void Gather()` |
| `rollback_synchronizer.set_schema({...})` | `SetSchema(new Dictionary<string, NetworkSchemaSerializer> {...})` |
| `NetworkSchemas.vec3f32()` | `NetworkSchemas.Vec3F32()` |
| `visibility_filter.add_visibility_filter(func(peer): ...)` | `VisibilityFilter.AddVisibilityFilter(peer => ...)` |
| `snake_case` everywhere | `PascalCase` everywhere |

Property paths in scenes and schemas keep their original form - `":position"`, `"Input:movement"` - except that they
name C# properties, so the part after the colon is `PascalCase` for your own properties and stays Godot's
`snake_case` for engine ones (`:position`, `:velocity`, `:rotation`).

## Three things that will catch you

**Events are not disconnected when a node is freed.** Godot does that for signals and cannot for C# events. Store
the delegate and unsubscribe:

```csharp
private Action<double, int>? _handler;

public override void _Ready()
{
    _handler = (delta, tick) => { /* ... */ };
    NetworkTime.Instance.BeforeTick += _handler;
}

public override void _ExitTree()
{
    if (_handler is not null && NetworkTime.Instance is { } time) time.BeforeTick -= _handler;
}
```

The three synchronizers do this for you; anything you subscribe yourself is yours to clean up.

**`Variant` has no value equality.** `variantA == variantB` compares boxes, not values. Use `VariantComparer.Instance`
or `Snapshot.ValueComparer`.

**Attributes only work on properties you declare.** `position` and `velocity` come from `CharacterBody3D`, so they
stay strings in the inspector. And they must be *properties*, not fields: Godot exposes a partial class's properties
to `Get`/`Set` and not its plain fields, so a field cannot be a netfox property at all.

## Deliberate deviations

Everything here differs from upstream on purpose. Each was a decision, not an accident of porting.

### Structure

- **`NetfoxContext`.** Upstream's servers are autoloads, so a process can run exactly one netfox stack. Here the
  autoloads still exist and still fill `Instance`, but they register into `NetfoxContext.Default`, and a
  `NetfoxContextRoot` node gives its subtree a second stack. That is what lets a test run a host and a client in one
  tree, which is where most of this port's bugs were found.
- **`NetfoxSettings`.** All `netfox/*` project settings read once into one mutable object, swappable before the
  autoloads construct. Upstream reads `ProjectSettings` at each use site.
- **`BaseSynchronizer`.** `RollbackSynchronizer`, `PredictiveSynchronizer` and `StateSynchronizer` repeat the same
  context resolution, dirty-property handling and reconnect logic upstream; here it lives in a shared base. Nothing
  becomes public API that was not already.
- **Interfaces instead of duck typing.** `has_method("_rollback_tick")` becomes `node is IRollbackTick`. Same
  behaviour, checked by the compiler.
- **Explicit command ids.** Upstream hands them out by `register_command` in autoload `_ready` order, so the wire
  protocol depends on load order. Here they are constants in `CommandIds`, with `FirstUserCommand = 32` for yours.

### Corrections

- **`Sanitize` works.** Upstream's `snapshot.gd` erases by the array of invalid subjects rather than by each subject,
  so it removes nothing - a peer can send state and input for nodes it does not own. Here it removes them.
- **State is sent once per loop.** Upstream sends state inside the resimulation loop, once per resimulated tick, so
  a client on 100ms of latency costs the host twice the traffic of one on none. Here the newest tick is sent once
  per loop.
- **Servers reset between sessions.** Upstream keeps recorded history and per-peer ids across a session ending, so a
  second session that starts at tick 0 after a long first one has every write land outside the history window and
  silently dropped. `NetworkEvents` now calls `NetfoxContext.ResetSession` when the session stops.
- **Input reaches peers that need it.** Upstream sends input only to the authority of the nodes it controls, so a
  peer owning one input node of a subject never receives the others and stops simulating it entirely
  ([foxssake/netfox#236](https://github.com/foxssake/netfox/issues/236)). Here it also goes to the owners of the
  sibling input nodes.
- **An unknown identity costs one frame.** Upstream's sparse reader abandons the whole packet at a reference it
  cannot resolve, losing every node written behind it. And a peer that has not acked a subject is diffed against a
  baseline it never received. Both fixed; see [foxssake/netfox#563](https://github.com/foxssake/netfox/issues/563).
- **Redundant input is diffed.** Upstream writes N full snapshots per input packet
  ([foxssake/netfox#560](https://github.com/foxssake/netfox/issues/560)); here the newest goes in full and the older
  ones as diffs against it. Three ticks of held input cost 87 bytes rather than 213.
- **Identities are named relative to the multiplayer root**, not absolutely, so two stacks in one tree agree on
  names.
- **History warnings name the subject**, not just the tick
  ([foxssake/netfox#514](https://github.com/foxssake/netfox/issues/514)).

### Not ported

- **noray, nohub, trimsock.** NAT punchthrough and relay. Out of scope.
- **`.off` driver file toggles.** Upstream ships physics drivers with a disabled extension so they do not compile;
  here they are ordinary classes that report themselves unavailable when their engine is missing.
- **Dead code.** `property-cache.gd`, `property-config.gd`, `property-snapshot.gd` and `bimap.gd` have no references
  upstream and are not here.

### Extra

- **Property attributes.** `[RollbackState]` and friends, from a source generator, so paths come from symbols rather
  than from strings. Optional; the interfaces work exactly as upstream's methods do.
- **A peer factory on `NetworkSimulator`**, so autoconnect works with something other than ENet.
- **`StartProxy`**, so the latency and loss proxy can be used without the editor-only autoconnect flow.

## Wire compatibility

There is none, and there is not meant to be. A GDScript netfox peer and a netfox.cs peer cannot talk to each other -
the command ids differ, and so does the redundant input encoding. Both ends of a session have to be this port.
