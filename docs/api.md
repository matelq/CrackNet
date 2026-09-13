# API reference

Generated from the XML doc comments by `docs/generate-api.py`. The [guides](README.md) are the place to
start; this is the index for when you know roughly what you want and not what it is called.

## Netfox

### BaseSnapshotSerializer

Shared identity and schema handling for snapshot serializers. Port of serializers/base-snapshot-serializer.gd.

| | Member | Summary |
|---|---|---|
| method | `PacketsFor(System.Int32)` | The shared packet buffer, set up to prefix each packet with `tick`. |
| method | `ReadProperty(Godot.Node,Godot.NodePath,Netfox.Core.Serialization.ByteReader)` | Returns Nil if the buffer ends before the property could be read. |

### BaseSynchronizer

Plumbing shared by `RollbackSynchronizer`, `PredictiveSynchronizer` and `StateSynchronizer`: context resolution, deferred reprocessing, reprocess-on-connect and its teardown, managed node discovery and schema registration. Upstream has no such base; rollback-synchronizer.gd, predictive-synchronizer.gd and state-synchronizer.gd repeat all of it. Only the parts that are identical in every synchronizer live here, and nothing is added to a synchronizer's public API by inheriting: the extras stay protected.

| | Member | Summary |
|---|---|---|
| property | `Context` | The netfox stack this node uses; resolved when it enters the tree. |
| method | `ClearSchemaInternal` | Drops every serializer registered through this synchronizer, falling back to variant encoding. |
| method | `CollectManagedNodes(Godot.Node)` | Every descendant of `root`, except branches rooted by another synchronizer. |
| method | `IsForeignRoot(Godot.Node)` | True when another synchronizer of the same kind treats `node` as its root. |
| method | `MarkPropertiesDirty` | Queues a deferred `ProcessSettings`; call after adding property paths at runtime. |
| method | `MergeSchemaInternal(System.Collections.Generic.IReadOnlyDictionary{System.String,Netfox.NetworkSchemaSerializer})` | Registers serializers for the given "Node:property" paths, keeping the ones already set. |
| method | `OnSessionReset` | The servers have dropped the ticks of the session that just ended, so everything this synchronizer registered has to be registered again, at the new session's ticks. |
| method | `ProcessSettings` | (Re)registers everything this synchronizer manages with the servers. |
| method | `ReprocessOnConnect` | Processes settings again once the peer connects: pre-placed nodes start owned by us (the offline peer 1) and only then change owner. The user may swap out `multiplayer`, which NetworkEvents already handles. |
| method | `ResolveRoot` | The node property paths are relative to: the synchronizer's Root, defaulting to the parent. |
| method | `SetSchemaInternal(System.Collections.Generic.IReadOnlyDictionary{System.String,Netfox.NetworkSchemaSerializer})` | Replaces the schema of every property registered so far. |
| method | `StopReprocessOnConnect` | Undoes `ReprocessOnConnect`. Godot disconnects the signals of a freed node on its own, C# events are not, so every synchronizer must do this when it leaves the tree. |

### CommandIds

Well-known command ids. Explicit so they do not depend on autoload order.

| | Member | Summary |
|---|---|---|
| field | `FirstUserCommand` | First id handed out by RegisterCommand for user commands. |
| field | `InputAck` | Reworked only: the newest input tick a peer has, below which nothing is missing. See netfox-net#40. |

### DenseSnapshotSerializer

Full state: every registered property of every auth subject. Port of serializers/dense-snapshot-serializer.gd.

### GodotDataExtensions

The engine-touching half of the Core data types: reading and writing node properties, authority checks.

| | Member | Summary |
|---|---|---|
| method | `Apply(Netfox.Core.Data.Snapshot{Godot.Node,Godot.NodePath,Godot.Variant})` | Writes every stored value back onto its subject. |
| method | `Sanitize(Netfox.Core.Data.Snapshot{Godot.Node,Godot.NodePath,Godot.Variant},System.Int32)` | Drops every subject that `sender` does not have authority over. |
| method | `SetFromPaths(Netfox.Core.Data.PropertyPool{Godot.Node,Godot.NodePath},Godot.Node,System.Collections.Generic.IEnumerable{System.String})` | Replaces the pool contents with the parsed "node:property" paths relative to `root`. |

### IInterpolatedProperties

Editor-time property discovery, replaces _get_interpolated_properties.

### IRollbackDespawnAware

Called when rollback restores a tick where this node is not alive. Replaces _rollback_despawn.

### IRollbackDestroyAware

Called once the node can no longer be respawned by rollback. Replaces _rollback_destroy; the default is QueueFree.

### IRollbackInputProperties

Editor-time property discovery, replaces _get_rollback_input_properties.

### IRollbackSpawnAware

Called when rollback restores a tick where this node is alive after having been despawned. Replaces _rollback_spawn.

### IRollbackStateProperties

Editor-time property discovery, replaces _get_rollback_state_properties.

### IRollbackTick

Implemented by nodes that take part in rollback simulation. Replaces the duck-typed _rollback_tick.

### ISynchronizedStateProperties

Editor-time property discovery, replaces _get_synchronized_state_properties.

### InterpolatedAttribute

Marks a member as interpolated between ticks by a TickInterpolator. The declaring type has to be partial; the generator implements `IInterpolatedProperties` on it. Properties only: Godot exposes a partial class's properties to Get and Set, but not its plain fields.

### InterpolationServer

Keeps from/to snapshots per subject and interpolates them every rendered frame. Port of servers/interpolation-server.gd.

| | Member | Summary |
|---|---|---|
| property | `Context` | The stack this server belongs to; resolved when it enters the tree. |
| method | `PushState(Godot.Node)` | Rotate states: the previous target becomes the source, the current values become the target. |
| method | `Register(Godot.Node,Godot.NodePath,Netfox.Interpolators.Interpolator)` | Register a property for interpolation. The interpolator defaults to one matching the current value type. |
| method | `SetServerEnabled(System.Boolean)` | Global toggle; off by default in headless mode. |
| method | `Teleport(Godot.Node)` | Skip interpolation for the subject until the next tick loop. |

### Interpolators

Registry of value interpolators by type. Port of interpolators.gd.

| | Member | Summary |
|---|---|---|
| property | `DefaultApply` | Fallback: snap to whichever endpoint is closer. |
| method | `Register(System.Func{Godot.Variant,System.Boolean},System.Func{Godot.Variant,Godot.Variant,System.Double,Godot.Variant})` | Registered interpolators take precedence over earlier ones. |

### NetfoxContext

The netfox servers as one owned graph instead of process-wide singletons. Upstream has no equivalent: its servers are GDScript autoloads, so a process can only ever run one netfox stack. The autoloads still exist here and still fill `Instance`, but they register into `Default`, and a second stack can be created by adding a `NetfoxContextRoot` to the tree: everything below it resolves to that context instead, which is what an in-process two-peer test needs.

| | Member | Summary |
|---|---|---|
| property | `Default` | The context the autoloads register into, and the one nodes outside a `NetfoxContextRoot` use. |
| property | `IsDefault` | True for the context the autoloads live in; only its servers are published as `Instance`. |
| method | `CreateServers(Godot.Node)` | Creates the servers this context is missing as children of `parent`, in the same order the plugin registers the autoloads in (dependencies first). Does nothing for the default context, whose servers are the autoloads themselves. |
| method | `For(Godot.Node)` | The context `node` belongs to: the nearest `NetfoxContextRoot` above it, or `Default`. Nodes resolve this once, when they enter the tree. |
| method | `ResetSession` | Drops everything tied to the session that just ended: recorded history, what was sent to which peer, the ids exchanged with peers, spawn ticks. Registrations survive, so a scene that stays in the tree keeps working. Called automatically when `NetworkEvents` sees the session stop. Games that disable NetworkEvents have to call it themselves; without it, the next session starts at tick zero while the histories still hold the previous session's ticks, and every write lands outside their window and is dropped. |
| event | `SessionReset` | Raised at the end of `ResetSession`, once the servers have dropped their per-session data. Nodes that hold ticks of their own, like the synchronizers, listen to this to re-register themselves. |

### NetfoxContextRoot

Marks its subtree as belonging to `Context`: nodes below it use that context's servers instead of the autoloads. Creates the servers as its own children when it enters the tree.

| | Member | Summary |
|---|---|---|
| property | `Context` | The context this subtree uses. Assign before entering the tree to share one between roots. |

### NetfoxSettings

Every `netfox/*` project setting, read once into one mutable object. Upstream reads `ProjectSettings` in field initializers of each server (see `network-time.gd:370`), which ties the servers to `project.godot` and makes runtime toggles ad hoc. Servers take their values from `Instance` instead; assign a different instance before the autoloads are created to configure them.

| | Member | Summary |
|---|---|---|
| property | `Instance` | The settings the autoloads use. Replace before they enter the tree; mutating it later only affects re-reads. |
| property | `MaxInputRedundancy` | The most input ticks one packet may carry. `InputRedundancy` is the floor and this is the ceiling: in between, the window is whatever the receiving peer has not acknowledged yet. It exists to bound the packet rather than the redundancy. A peer that has heard nothing for a long time would otherwise try to send its whole history in one go, which does not fit and would be dropped whole - turning a bad link into no link at all. |
| property | `SyncPanicThreshold` | Same `netfox/time/recalibrate_threshold` key as `RecalibrateThreshold`, but with the fallback upstream uses in the time synchronizer (`network-time-synchronizer.gd:105`). The two differ only when the setting is absent. |
| method | `Load` | Reads every setting from `ProjectSettings`, falling back to the defaults the plugin registers. |

### NetworkCommandServer

Transmits commands over the network: a single id byte plus raw binary data, either over RPC (default) or as raw SceneMultiplayer packets. Port of servers/network-command-server.gd.

| | Member | Summary |
|---|---|---|
| property | `Context` | The stack this server belongs to; resolved when it enters the tree. |
| property | `SentCounts` | Payload bytes and packets sent per command id since the last `ResetSentCounts`. Transport framing is not included, so this says what netfox asked for rather than what went on the wire. It exists because a total cannot answer the question that matters when something grows: which command grew. |
| field | `PacketPrefix` | Prefix of raw command packets: NUL, n, f. |
| method | `IsCommandPacket(System.ReadOnlySpan{System.Byte})` | True if `packet` is a command packet. Always true when commands go over RPC. |
| method | `RegisterCommand(System.Action{System.Int32,System.Byte[]},Godot.MultiplayerPeer.TransferModeEnum,System.Int32)` | Register a command at the next available id. |
| method | `RegisterCommandAt(System.Int32,System.Action{System.Int32,System.Byte[]},Godot.MultiplayerPeer.TransferModeEnum,System.Int32)` | Register a command at a specific id. Registering the same id twice is an error. |

### NetworkEvents

Convenience multiplayer lifecycle events that survive MultiplayerAPI swaps, and automatic NetworkTime start/stop. Port of network-events.gd.

| | Member | Summary |
|---|---|---|
| property | `Context` | The stack this server belongs to; resolved when it enters the tree. |
| property | `Enabled` | Events are only emitted while enabled. Initial value comes from netfox/events/enabled. |
| method | `StopClient` | Emits OnClientStop at most once per session, however the session ended. |
| event | `OnClientStart` | (own peer id) |
| event | `OnMultiplayerChange` | (old, new) |

### NetworkHistoryServer

Tracks the history of rollback state, rollback input, and synchronized state properties. Port of servers/network-history-server.gd.

| | Member | Summary |
|---|---|---|
| property | `Context` | The stack this server belongs to; resolved when it enters the tree. |
| method | `Deregister(Godot.Node)` | Stop tracking every property of `node` and drop its history. |
| method | `GetInputAgeFor(System.Collections.Generic.IEnumerable{Godot.Node},System.Int32)` | Age in ticks of the latest rollback input of any of the subjects, or -1. |
| method | `GetLatestInputFor(System.Collections.Generic.IEnumerable{Godot.Node},System.Int32)` | Latest tick at or before `tick` where any of the subjects has rollback input, or -1. |
| method | `GetLatestStateTickFor(System.Collections.Generic.IEnumerable{Godot.Node},System.Int32)` | Latest tick at or before `tick` where any of the subjects has rollback state, or -1. |
| method | `GetStateAgeFor(System.Collections.Generic.IEnumerable{Godot.Node},System.Int32)` | Age in ticks of the latest rollback state of any of the subjects, or -1. |
| method | `Ignore(Godot.Node)` | Do not record `subject` until FlushIgnores, which runs after every rollback tick. |
| method | `PushRollbackState(Godot.Node,System.Int32)` | Record the registered rollback state properties of `subject` at `tick`, e.g. to seed spawn state. |
| method | `ResetSession` | Drops every recorded tick, keeping which properties are registered. Called on session reset. |

### NetworkIdentityServer

Tracks network identities: nodes are referenced by scene path, replaced with compact numeric ids negotiated per peer. Port of servers/network-identity-server.gd.

| | Member | Summary |
|---|---|---|
| property | `Context` | The stack this server belongs to; resolved when it enters the tree. |
| method | `ErasePeer(System.Int32)` | Free all identity data associated with `peer`. |
| method | `FlushQueue` | Broadcast all queued identities. Called automatically by NetworkTime. |
| method | `IdentityPathOf(Godot.Node)` | Nodes are named by their path below the multiplayer root ("/root" unless the game sets its own), the same way RPCs address them. Upstream uses the absolute path (network-identity-server.gd:74), which assumes a single netfox stack per process; relative paths let two stacks in one tree agree on names. |
| method | `QueueIdentifierFor(Godot.Node,System.Int32)` | Queue sending the numeric id of `node` to `peer`. Sent on the next FlushQueue. |
| method | `RegisterNode(Godot.Node)` | Register a node so it can be referred to over the network. Must be registered with the same path on all peers. |
| method | `ResetSession` | Forgets the ids exchanged with peers, keeping the nodes registered locally. |
| method | `ResolveReference(System.Int32,Netfox.Core.Data.NetworkIdentityReference,System.Boolean)` | Resolves a reference received from `peer`. Ids are in our local id space; names queue our id for that peer. |

### NetworkPerformance

Custom Performance monitors for the network and rollback loops. Port of network-performance.gd.

| | Member | Summary |
|---|---|---|
| property | `Context` | The stack this server belongs to; resolved when it enters the tree. |

### NetworkRollback

Runs the rollback loop: restore history, resimulate, record, broadcast. Port of rollback/network-rollback.gd.

| | Member | Summary |
|---|---|---|
| property | `Context` | The stack this server belongs to; resolved when it enters the tree. |
| property | `DisplayOffset` | Show this many ticks in the past to hide corrections. From netfox/rollback/display_offset. |
| property | `DisplayTick` | The tick shown on screen after the rollback loop. |
| property | `EnableDiffStates` | Whether to send only changed properties. From netfox/rollback/enable_diff_states. |
| property | `Enabled` | Whether the rollback loop runs at all. From netfox/rollback/enabled. |
| property | `HistoryLimit` | How many ticks back history is kept and rollback can go. From netfox/rollback/history_limit. |
| property | `HistoryStart` | First tick that can still be rolled back to. |
| property | `InputDelay` | Record inputs this many ticks into the future. From netfox/rollback/input_delay. |
| property | `InputRedundancy` | How many past inputs are resent with every input packet, at least 1. |
| property | `RollbackFrom` | First tick of the current rollback loop, -1 outside of it. |
| property | `Tick` | The tick currently being resimulated. Only meaningful during rollback. |
| method | `AfterTick(System.Int32)` | Records and sends input for the tick. Called by NetworkTime after every tick. |
| method | `GetLatestInputTick(Godot.Node)` | Latest tick with input available for `node`, or -1. |
| method | `IsRollback` | True while the rollback loop is running. |
| method | `Mutate(Godot.GodotObject,System.Nullable{System.Int32})` | Mark `target` as changed from `tick` on, so it gets resimulated even without input. |
| method | `NotifyResimulationStart(System.Int32)` | Request resimulation from `tick`; the earliest request wins. Call from BeforeLoop. |
| method | `ReportUnreproducedMutations(System.Collections.Generic.HashSet{Godot.Node},System.Int32)` | Mutations are expected to be a function of state and input, so resimulating a tick should produce them again. One that does not come back is either a legitimate change of outcome, or a one-off effect that the resimulation has just silently dropped, which is the hard-to-find half of foxssake/netfox#383. Traced, not warned, because only the second case is a problem and the two cannot be told apart from here. |
| method | `ResetSession` | Drops the state left over from the session that just ended. |
| method | `Rollback` | The rollback loop. Called by NetworkTime after every tick loop. |
| event | `AfterLoop` | Emitted after the rollback loop. |
| event | `AfterPrepareTick` | Emitted after state is restored for the tick. |
| event | `AfterProcessTick` | Emitted after the tick is simulated. |
| event | `BeforeLoop` | Emitted before the rollback loop; call NotifyResimulationStart here. |
| event | `OnPrepareTick` | Emitted before state is restored for the tick. |
| event | `OnProcessTick` | Emitted before the tick is simulated. |
| event | `OnRecordTick` | Emitted before the resulting state is recorded; carries tick + 1. |

### NetworkSchemaSerializer

Base class for schema serializers. Encode a Variant into a ByteWriter, decode it back from a ByteReader. Extend to implement custom serializers and pass them to RollbackSynchronizer.SetSchema. Port of schemas/network-schema-serializer.gd.

| | Member | Summary |
|---|---|---|
| field | `_quantizeBuffer` | Reused across calls, because this runs on the record path for every schema'd property every tick. Per thread rather than shared: nothing here is synchronized, and a second thread encoding into the same buffer would corrupt both answers rather than merely slow them down. |
| method | `Quantize(Godot.Variant)` | The value as it will come back out the other end. For a lossy schema this is not the value that went in, and that difference is the point: a peer recording what it actually has and every other peer recording what it was sent are then simulating from two different numbers for the same tick. The error is tiny - half precision is about 5e-4 relative - but it is systematic rather than noise, so it never averages out and produces a steady trickle of corrections no amount of bandwidth removes. State Synchronization prescribes exactly this: quantize the simulation as if it had been sent, on both sides. The default round trips through `ByteWriter` and `ByteReader`, so a custom serializer is correct without doing anything. Override it where the answer is cheaper to compute directly, or where the encoding is lossless and the whole round trip can be skipped. |

### NetworkSchemas

Factory of schema serializers. Port of schemas/network-schemas.gd; naming follows the original (uint16, vec3f32, ...). `Variant` and `String` read like the types of the same name on purpose: they are kept as upstream names them, so the upstream schema documentation applies here unchanged. C# resolves the two without ambiguity, and renaming them would cost that mapping for a cosmetic gain.

| | Member | Summary |
|---|---|---|
| method | `ArrayOf(Netfox.NetworkSchemaSerializer,Netfox.NetworkSchemaSerializer)` | Godot Array with a size prefix, each item with `item`. |
| method | `CString` | UTF-8 string terminated by a zero byte. |
| method | `Degrees8` | Angle in degrees, wrapped to [0, 360) and quantized to 8 bits. |
| method | `Dictionary(Netfox.NetworkSchemaSerializer,Netfox.NetworkSchemaSerializer,Netfox.NetworkSchemaSerializer)` | Godot Dictionary with a size prefix. |
| method | `Normal2T(Netfox.NetworkSchemaSerializer)` | Unit Vector2 as a single angle. |
| method | `Normal3T(Netfox.NetworkSchemaSerializer)` | Unit Vector3 as two octahedron-encoded components. |
| method | `Radians8` | Angle in radians, wrapped to [0, TAU) and quantized to 8 bits. |
| method | `Sfrac8` | Signed fraction in [-1, 1] quantized to 8 bits. |
| method | `String` | UTF-8 string prefixed by a 32-bit length. |
| method | `Ufrac8` | Unsigned fraction in [0, 1] quantized to 8 bits. |
| method | `Variant` | Any type supported by GD.VarToBytes; size depends on the value. |
| method | `Varuint` | Variable-length unsigned integer, 1 to 10 bytes. |

### NetworkSynchronizationServer

Synchronizes rollback input, rollback state and synchronized state over the network, respecting visibility filters and schemas, with optional diff states. Packets are sent per tick. Port of servers/network-synchronization-server.gd.

| | Member | Summary |
|---|---|---|
| property | `Context` | The stack this server belongs to; resolved when it enters the tree. |
| property | `EnableInputBroadcast` | When off, inputs only go to the authorities of the nodes they control. From netfox/rollback/enable_input_broadcast. |
| field | `AckInterval` | Ticks between acknowledgements. One per tick per peer would be half as many packets again on the host for four bytes each; a stale acknowledgement only costs a few repeated input ticks, which are patches against the newest and nearly free. |
| field | `_inputAcknowledged` | What each peer we send input to last told us it had. Absent means it has told us nothing yet. |
| field | `_inputReceived` | What each sender has got through to us, so we can tell it what it no longer has to repeat. |
| method | `AcknowledgeInput(System.Int32)` | Tells every peer that has sent us input how far it has got through, so it can stop repeating what we already have. Called once per tick alongside `Int32`, and rate limited from there. |
| method | `Deregister(Godot.Node)` | Deregister every setting associated with `node`. |
| method | `ErasePeer(System.Int32)` | Erase everything kept about `peer`. Called by default when a peer leaves. |
| method | `InputWindowFor(System.Int32,System.Int32,Netfox.NetworkHistoryServer)` | The input ticks to send one peer: everything it has not acknowledged, floored at the configured redundancy and capped so the packet still fits. A fixed count is generous against independent loss and worth nothing against a burst - three in a row go missing 0.1% of the time at 10% loss, but a burst takes all three every time, and the authority is left predicting for the length of the outage. Sending what has not been acknowledged instead is what Deterministic Lockstep does, and it is smaller in the ordinary case as well as larger in the bad one: the floor only applies because an acknowledgement can itself be lost. |
| method | `MakePeerSnapshot(Netfox.Core.Data.Snapshot{Godot.Node,Godot.NodePath,Godot.Variant},System.Int32,Netfox.Core.Data.PropertyPool{Godot.Node,Godot.NodePath},System.Func{Godot.Node,System.Boolean})` | Snapshot to send to `peer`: only visible subjects and their auth properties. |
| method | `Quantize(Godot.Node,Godot.NodePath,Godot.Variant)` | The value as this property's schema will deliver it, so what a peer records for itself is what every other peer will be told. Properties with no schema of their own are returned untouched, which is nearly all of them. |
| method | `ReconcileAuthority` | Re-sorts every registered property into or out of the owned pools by what its node's authority is now. Called once per tick before anything is sent. Registration sorted by authority once, and Godot has no signal for it changing, so a SetMultiplayerAuthority after that point left the pools describing the past: the peer that gained authority recorded state as real - the history server reads authority live - but never sent it, and the peer that lost it kept sending. Two servers disagreeing about one node, quietly. Anything that hands an object over at runtime hits this: a respawn onto another peer, possessing a character, a pickup whose truth should live with whoever holds it. A dozen dictionary lookups a tick for a room of four. Upstream has the same shape and the same gap (network-synchronization-server.gd:73), so this is inherited, and the fix lives here rather than in a helper callers would have to remember - the point is that nobody has to (netfox-net#45). |
| method | `ResetSession` | Drops what was sent to whom, keeping registrations, schemas and visibility filters. |
| method | `SynchronizeStateRange(System.Int32,System.Int32)` | Sends every subject at the newest tick of the range it is authoritative for, rather than all of them at the newest tick of the range. The distinction is the difference between working and not. State is only sent for subjects the sender is authoritative for, and a node driven by a remote peer's input is predicted at the newest tick - that peer's input for it is still a round trip away - so it is not authoritative there and nothing goes out for it. Sending only the newest tick therefore sent such a node nothing at all, ever, and the peer driving it never converged (netfox-net#35). Upstream sends every tick of the range instead (network-rollback.gd:429), which is correct but multiplies state traffic by the length of the range, every frame (#29). One tick per distinct answer is both: two, in a session where the host drives its own player and one remote player. |
| method | `WithoutUnacknowledgedSubjects(Netfox.Core.Data.Snapshot{Godot.Node,Godot.NodePath,Godot.Variant},System.Int32)` | The diff baseline minus the subjects `peer` cannot resolve yet, so those go out in full. A peer acks a subject by sending back an id for it, which it can only do once it has resolved that subject's name - that is, once the node exists on its side. Until then it drops our frames, and diffing against a baseline it never received would leave it with a node missing every property that happens not to change, until the next full state. Upstream has no such guard (foxssake/netfox#563). |
| event | `OnInput` | Emitted when new input for a new subject was received. |
| event | `OnState` | Emitted when state was received. |

### NetworkTickrateHandshake

Exchanges the configured tickrate with the host when peers join. Port of time/network-tickrate-handshake.gd.

| | Member | Summary |
|---|---|---|
| property | `Context` | The netfox stack this node uses; resolved when it enters the tree. |
| method | `IsAuthority` | Overridable to ease testing; pretending to be a client is messy from a unit test. |
| method | `Run` | Run the handshake: broadcast tickrate, and send it to every joining peer. Called by NetworkTime. |
| event | `OnTickrateMismatch` | (peer, tickrate) |

### NetworkTime

Drives network ticks and keeps them synced to the host. Port of network-time.gd.

| | Member | Summary |
|---|---|---|
| property | `ClockOffset` | Reference clock minus simulation clock. |
| property | `ClockStretchFactor` | Current clock speed multiplier; above 1.0 speeds up to catch the host, below slows down. |
| property | `Context` | The stack this server belongs to; resolved when it enters the tree. |
| property | `PhysicsFactor` | Multiplier from physics-process speeds to tick speeds; multiply velocities by it around MoveAndSlide. |
| property | `RemoteClockOffset` | Same as NetworkTimeSynchronizer.RemoteOffset. |
| property | `RemoteRtt` | Estimated roundtrip time to the server. Always 0 on the server. |
| property | `StallThreshold` | Seconds without frames before the game is considered stalled and catch-up ticks are skipped. |
| property | `Tick` | Current network time in ticks, continuously synced with the server. |
| property | `TickFactor` | 0.0 right after a tick, 1.0 right before the next. |
| property | `Tickrate` | Ticks per second. Equals the physics tickrate when SyncToPhysics is on. |
| property | `Ticktime` | Duration of a single tick, in seconds. |
| property | `Time` | Current network time in seconds, continuously synced with the server. |
| method | `IsClientSynced(System.Int32)` | Whether the given client finished its time sync. Only meaningful on the server. |
| method | `Start` | Start NetworkTime: synchronize with the host, then emit ticks. On clients, ticks start after the initial sync. Returns Ok, AlreadyInUse if already running, or Unavailable without a multiplayer peer. |
| method | `Stop` | Stop NetworkTime and the background sync. No ticks until the next Start. |
| event | `AfterClientSync` | Emitted on the server when a client finishes its time sync. (peer id) |
| event | `AfterSync` | Emitted after time is synchronized; instantly on the server. |
| event | `AfterTick` | (delta, tick) |
| event | `AfterTickLoop` | Emitted after the tick loop is run. |
| event | `BeforeTick` | (delta, tick) |
| event | `BeforeTickLoop` | Emitted before a tick loop is run. |
| event | `OnTick` | (delta, tick) |
| event | `OnTickrateMismatch` | (peer, tickrate). Emitted when the tickrate mismatch action is Signal. |

### NetworkTimeSynchronizer

Continuously synchronizes the reference clock to the host. Transport and timing live here, the clock math lives in Netfox.Core.Time.ClockSynchronizer. Port of network-time-synchronizer.gd.

| | Member | Summary |
|---|---|---|
| property | `Context` | The stack this server belongs to; resolved when it enters the tree. |
| property | `RemoteOffset` | Estimated offset from the host clock. Positive means the host is ahead. |
| property | `Rtt` | Measured roundtrip time to the host; actual values are within Rtt +/- RttJitter. |
| property | `SyncInterval` | Time between sync samples in seconds, never below MinSyncInterval. Configured in project settings. |
| method | `GetTime` | Current time of the reference clock, in seconds. |
| method | `Start` | Start the sync loop. Starting multiple times has no effect. |
| event | `OnInitialSync` | Emitted once the initial timestamp is received and the sync loop starts. |
| event | `OnPanic` | Emitted when clocks are so far apart that the clock gets hard-reset. Carries the offset. |

### PeerVisibilityFilter

Decides which peers can see a synchronized node. Port of peer-visibility-filter.gd.

| | Member | Summary |
|---|---|---|
| property | `Context` | The netfox stack this node uses; resolved when it enters the tree. |
| method | `GetRpcTargetPeers` | Peer ids to pass to RpcId: a broadcast, a single exclusion (negative id), or an explicit list. |
| method | `SetVisibilityFor(System.Int32,System.Boolean)` | Peer 0 sets the default visibility. |
| method | `UpdateVisibility(System.Collections.Generic.IReadOnlyList{System.Int32})` | Recomputes visible peers. Defaults to the current multiplayer peers. |

### PredictiveSynchronizer

RollbackSynchronizer without networking: keeps rollback state history and simulates nodes locally, for short-lived or deterministic objects. Port of rollback/predictive-synchronizer.gd.

| | Member | Summary |
|---|---|---|
| property | `Root` | Node the property paths are relative to; defaults to the parent. |
| property | `StateProperties` | State property paths in "Node:property" form, relative to Root. |
| method | `AddState(System.Object,System.String)` | Add a state property at runtime. Node may be a string, NodePath or Node relative to Root. |

### PropertyEntry

Parses "Node/Path:property" strings relative to a root node. Port of properties/property-entry.gd.

| | Member | Summary |
|---|---|---|
| method | `MakePath(Godot.Node,System.Object,System.String)` | Builds a "node:property" path string. Node may be a string, NodePath, or Node relative to `root`. |
| method | `Parse(Godot.Node,System.String)` | The part before the colon is a node path relative to `root`, the rest is the property. |

### RedundantSnapshotSerializer

Packs several snapshots into one packet, for input redundancy. Port of serializers/redundant-snapshot-serializer.gd. The first snapshot goes in full and the rest only as how they differ from it, which is what upstream's TODO(#560) asks for: consecutive ticks of input are mostly identical, so a redundant copy is usually a few bytes of header. Every subject the older snapshot has still gets a frame, empty when nothing changed, so the reader can tell an unchanged subject from one that was not in that tick at all.

### RewindableAction

A replicated set of ticks at which a discrete event happened. Peers toggle it inside rollback ticks; the authority broadcasts the ground truth, and predictions get confirmed or cancelled. Port of rewindable-action.gd.

| | Member | Summary |
|---|---|---|
| property | `Context` | The netfox stack this node uses; resolved when it enters the tree. |
| method | `GetContext(System.Nullable{System.Int32})` | Arbitrary data remembered per tick, e.g. the projectile spawned by this action. |
| method | `HasCancelled` | True if any tick got cancelled during the last loop. |
| method | `HasConfirmed` | True if any tick got confirmed during the last loop. |
| method | `Mutate(Godot.GodotObject)` | Resimulate `target` whenever this action changes. |
| method | `ReceiveState(System.Byte[])` | Ingests the authoritative tickset; queues differences for the next loop. Exposed for tests. |
| method | `SetActive(System.Boolean,System.Nullable{System.Int32})` | Toggle the action for `tick`, defaulting to the current rollback tick. |

### RollbackInputAttribute

Marks a member as rollback input, gathered on its authority and sent to the peers that simulate it. The declaring type has to be partial; the generator implements `IRollbackInputProperties` on it. Properties only: Godot exposes a partial class's properties to Get and Set, but not its plain fields.

### RollbackLivenessServer

Tracks whether subjects were alive at a given tick, as a [spawn, despawn] interval. Port of servers/rollback-liveness-server.gd.

| | Member | Summary |
|---|---|---|
| property | `Context` | The stack this server belongs to; resolved when it enters the tree. |
| method | `Deregister(Godot.Node)` | Stops tracking; the current liveness of the subject is left as-is. |
| method | `DestroyOldSubjects(System.Nullable{System.Int32})` | Destroys subjects despawned before `thresholdTick`, which defaults to the rollback history start. |
| method | `IsAlive(Godot.Node,System.Int32)` | Unknown subjects are always alive. A despawned subject is still alive on its despawn tick and dead after it, so the deactivating game logic can run in rollback. |
| method | `Register(Godot.Node,System.Action,System.Action,System.Action,System.Nullable{System.Int32})` | Register `subject` for liveness tracking. The callbacks run when rollback needs to respawn, despawn, or finally destroy the subject. Destroy defaults to QueueFree; spawn tick defaults to the current rollback tick. |
| method | `ResetSession` | Respawns every registered subject at the current tick, dropping the previous session's spawn ticks. |
| method | `RestoreLiveness(System.Int32)` | Applies the liveness of every subject as it was on `tick`. |

### RollbackSimulationServer

Runs gameplay simulation during rollback: tracks which nodes participate, decides which need simulating for a tick (in scene tree order), and calls their callbacks. Port of servers/rollback-simulation-server.gd.

| | Member | Summary |
|---|---|---|
| property | `Context` | The stack this server belongs to; resolved when it enters the tree. |
| property | `RegisteredNodes` | Every node registered for simulation, in registration order. |
| method | `Deregister(Godot.Node)` | Remove `subject` from the rollback loop. |
| method | `DeregisterNode(Godot.Node)` | Remove `node` and all its input links and prediction settings. |
| method | `GetNodesToSimulate(Netfox.Core.Data.Snapshot{Godot.Node,Godot.NodePath,Godot.Variant})` | Nodes to simulate for the tick of `inputSnapshot`, in scene tree order. |
| method | `IsPredictingCurrent` | True if the node currently being simulated is predicted. |
| method | `Register(Godot.Node,System.Action{System.Double,System.Int32,System.Boolean})` | Register a rollback tick callback for `subject`. One callback per node. |
| method | `RegisterRollbackInputFor(Godot.Node,Godot.Node)` | Register `input` as providing input for `node`. |
| method | `Register``1(``0)` | Register a rollback-aware node. |
| method | `ResetSession` | Forgets which ticks were simulated, keeping registered nodes and their input graph. |
| method | `SetPredictionEnabledFor(Godot.Node,System.Boolean)` | Prediction means the node is simulated even without up to date input. |
| method | `Simulate(System.Double,System.Int32)` | Simulate a single rollback tick. |

### RollbackStateAttribute

Marks a member as rollback state, replicated and rolled back by a RollbackSynchronizer. The declaring type has to be partial; the generator implements `IRollbackStateProperties` on it. Properties only: Godot exposes a partial class's properties to Get and Set, but not its plain fields.

### RollbackSynchronizer

Configures rollback for a node tree: which properties are state, which are input, and which nodes get simulated. Registers everything with the netfox servers. Port of rollback/rollback-synchronizer.gd.

| | Member | Summary |
|---|---|---|
| property | `EnablePrediction` | Simulate managed nodes even without up to date input. |
| property | `InputProperties` | Input property paths in "Node:property" form, relative to Root. |
| property | `Root` | Node the property paths are relative to; defaults to the parent. |
| property | `SpawnTick` | Tick the managed nodes came to life. Defaults to the tick after entering the tree. |
| property | `StateProperties` | State property paths in "Node:property" form, relative to Root. |
| property | `UnlistedAttributeProperties` | Attribute properties under Root that this synchronizer does not replicate, as "Node:property" paths. Empty when the scene lists everything the code declares. |
| property | `VisibilityFilter` | Controls which peers receive state. Added as a child automatically, under its own name so that it reads as itself in a saved scene rather than as a generated one. |
| method | `AddInput(System.Object,System.String)` | Add an input property at runtime. Node may be a string, NodePath or Node relative to Root. |
| method | `AddState(System.Object,System.String)` | Add a state property at runtime. Node may be a string, NodePath or Node relative to Root. |
| method | `GetInputAge` | Age of the latest known input in ticks, or -1 if none. |
| method | `GetLastKnownInput` | Latest tick with input for this synchronizer, or -1. |
| method | `GetLastKnownState` | Age of the latest known state in ticks, or -1. |
| method | `HasInput` | Whether any input is known for the current rollback tick. |
| method | `IgnorePrediction(Godot.Node)` | Do not record the state of `node` during this rollback tick. |
| method | `IsPredicting` | True when the simulated node runs on guessed input, or, outside simulation, when current input is missing. |
| method | `ProcessAuthority` | Re-registers properties, picking up authority changes. Called on connect. |
| method | `ResolveRoot` | Re-reads the configuration and registers nodes for simulation, liveness, identity and visibility. |
| method | `SetSchema(System.Collections.Generic.IReadOnlyDictionary{System.String,Netfox.NetworkSchemaSerializer})` | Replace the serialization schema: property path to serializer. |
| method | `Spawn(System.Nullable{System.Int32})` | Mark the managed nodes as spawned at `tick` and seed their state. |
| method | `WarnAboutUnlistedAttributes(Godot.Node)` | A [RollbackState] or [RollbackInput] attribute is gathered into StateProperties/InputProperties by the editor plugin when the scene is saved - and at no other time. Headless, or with a scene saved before the property existed, the attribute is decoration: the property is never registered and nothing says so. That cost a day once (netfox-net#58): a new input never left the machine that pressed it, and a check passed anyway. So the same gather runs here at runtime, and every declared path the lists do not carry gets a warning naming it. The lists stay the source of truth; this only refuses to be quiet about the difference. |

### SparseSnapshotSerializer

Diff state: only properties present in the snapshot, flagged by a bitset. Port of serializers/sparse-snapshot-serializer.gd.

### StateSynchronizer

Replicates properties from their authority to every peer once per tick, without rollback. Port of state-synchronizer.gd.

| | Member | Summary |
|---|---|---|
| property | `Properties` | Property paths in "Node:property" form, relative to Root. |
| property | `Root` | Node the property paths are relative to; defaults to the parent. |
| property | `VisibilityFilter` | Controls which peers receive state. Added as a child automatically. |
| method | `AddState(System.Object,System.String)` | Add a property at runtime. Node may be a string, NodePath or Node relative to Root. |

### SynchronizedStateAttribute

Marks a member as state for a StateSynchronizer, replicated without rollback. The declaring type has to be partial; the generator implements `ISynchronizedStateProperties` on it. Properties only: Godot exposes a partial class's properties to Get and Set, but not its plain fields.

### TickInterpolator

Smooths the configured properties between network ticks. Port of tick-interpolator.gd.

| | Member | Summary |
|---|---|---|
| property | `Context` | The netfox stack this node uses; resolved when it enters the tree. |
| property | `EnableRecording` | Record state automatically after every tick loop. |
| property | `Properties` | Property paths in "Node:property" form, relative to Root. |
| property | `RecordFirstState` | Snap to the first recorded state instead of interpolating from defaults. |
| property | `Root` | Node the property paths are relative to; defaults to the parent. |
| method | `AddProperty(System.Object,System.String)` | Add a property at runtime. Node may be a string, NodePath or Node relative to Root. |
| method | `Teleport` | Skip interpolation for the next tick loop, e.g. after respawning. |

## Netfox.Core.Collections

### Bitset

Stores a list of booleans packed into bytes. Port of netfox.internals/bitset.gd.

### Graph`1

Directed graph with lookup in both directions. Port of netfox.internals/graph.gd.

| | Member | Summary |
|---|---|---|
| method | `Erase(`0)` | Removes every link touching `node`. |
| method | `GetLinkedFrom(`0)` | Nodes that `node` links to. |
| method | `GetLinkedTo(`0)` | Nodes that link to `node`. |

### HistoryBuffer`1

Maps ticks to arbitrary data, stored in a sliding ring buffer. Port of netfox.internals/history-buffer.gd.

### IntervalScheduler

Returns true on every nth IsNow() call. Port of netfox.internals/interval-scheduler.gd.

## Netfox.Core.Data

### NetworkIdentifier`1

Maps a subject to its local id and per-peer ids. Port of servers/data/network-identifier.gd.

| | Member | Summary |
|---|---|---|
| event | `OnId` | (peer, id) |

### NetworkIdentityReference

Either a compact numeric id or a full node name. Port of servers/data/network-identity-reference.gd.

### ObjectSnapshot`3

Snapshot data for a single object. Port of servers/data/object-snapshot.gd.

### PerObjectHistory`3

Per-object timeline of ObjectSnapshots. Port of servers/data/per-object-history.gd.

| | Member | Summary |
|---|---|---|
| method | `Clear` | Drops every recorded tick for every subject. Subjects get a fresh history on their next write. |

### PropertyPool`2

A set of properties, each belonging to a subject. Port of servers/data/property-pool.gd.

### Snapshot`3

Stores property values of multiple subjects, recorded for a specific tick. Port of servers/data/snapshot.gd. Engine-specific operations (record from / apply to the subject, authority checks) live in Netfox.Godot as extension methods.

| | Member | Summary |
|---|---|---|
| property | `ValueComparer` | Comparer used for value equality in patches, merges and Equals. Netfox.Godot sets a Variant-aware one. |
| method | `CopySubjectTo(`0,Netfox.Core.Data.Snapshot{`0,`1,`2})` | Copies the data of `subject` into `target`. If there is no data, erases it from target too. |
| method | `Sanitize(System.Func{`0,System.Boolean})` | Removes every subject for which `isValid` returns false. |

## Netfox.Core.Logging

### NetfoxLogger

Logger with per-module levels and context tags. Port of netfox.internals/logger.gd. Output sinks are pluggable so the core stays engine-agnostic; Netfox.Godot wires them to GD.Print / PushWarning / PushError.

## Netfox.Core.Serialization

### ByteReader

Little-endian reader over a byte span. Replaces the read side of StreamPeerBuffer. Throws EndOfStreamException on underflow.

| | Member | Summary |
|---|---|---|
| method | `GetPartialData(System.Int32)` | Reads up to `count` bytes; returns fewer if the buffer ends early, like get_partial_data. |

### ByteWriter

Growable little-endian byte buffer. Replaces the write side of StreamPeerBuffer.

| | Member | Summary |
|---|---|---|
| property | `WrittenMemory` | What has been written, without copying it. Only valid until the next write, which may reallocate. |
| method | `PutUtf8String(System.String)` | u32 byte length followed by UTF-8 bytes, matching StreamPeer.put_utf8_string. |

### CString

Zero-terminated UTF-8 string. Port of _CStringSerializer.

### IdentityPacketSerializer

Serializes (full name, local id) pairs, used when sending local ids to other peers. Port of serializers/identity-packet-serializer.gd.

### NetRef

Encodes a NetworkIdentityReference as varuint id, or 0 followed by a c-string name. Port of _NetworkIdentityReferenceSerializer.

### NetworkSchema`3

Maps (subject, property) to a serializer, with a fallback. Port of schemas/network-schema.gd.

### PacketBuffer

Packs data chunks into packets of a specified size. Multiple chunks may go into a single packet, as long as they fit MaxPacketSize. Port of serializers/packet-buffer.gd.

| | Member | Summary |
|---|---|---|
| property | `PacketSetup` | Called on every fresh packet before data is written, e.g. to add a header. |

### TicksetSerializer

Compact encoding of a set of active ticks within a range of at most 255 ticks. Port of serializers/tickset-serializer.gd.

### VarBits

Variable-length bitset: 7 bits per byte, high bit marks continuation. Decoded bit count is a multiple of 7. Port of _VariableBitsetSerializer.

### VarUint

Variable-length unsigned integer: 7 data bits per byte, high bit marks continuation. Port of _VaruintSerializer.

## Netfox.Core.Time

### ClockSample

One NTP-style ping/pong measurement. Port of network-clock-sample.gd.

| | Member | Summary |
|---|---|---|
| property | `Offset` | See RFC 5905 section 8: theta = ((t2 - t1) + (t3 - t4)) / 2. |

### ClockSynchronizer

Transport-free core of NetworkTimeSynchronizer: owns the reference clock, in-flight and completed samples, and disciplines the clock after every completed sample. Netfox.Godot drives it with ping/pong commands and a timer. Port of the algorithm in network-time-synchronizer.gd.

| | Member | Summary |
|---|---|---|
| property | `RemoteOffset` | Estimated remaining offset to the remote clock after the last nudge. |
| property | `SyncInterval` | Seconds between samples, never below MinSyncInterval. |
| method | `BeginSample` | Records ping_sent for a new sample and returns its index, to be echoed back in the pong. |
| method | `CompleteSample(System.Int32,System.Double,System.Double)` | Completes an in-flight sample and disciplines the clock. Returns null if the sample was dropped by a panic. |
| method | `Reset` | Resets clock and sample state for a new sync session. |
| event | `OnPanic` | Emitted with the offending offset when the clock is hard-reset. |

### Clocks

Wall-clock time sources. Port of time/network-clocks.gd.

| | Member | Summary |
|---|---|---|
| method | `UnixTime` | Unix time in seconds with Stopwatch resolution. Equivalent of Time.get_unix_time_from_system(). |

### SteppingClock

Simulation clock: advanced manually by wall-clock deltas, optionally stretched.

### SystemClock

Reference clock: raw wall time plus an adjustable offset.

### TickClock

The arithmetic of the NetworkTime tick loop without any side effects: clock stretching towards a reference time, stall detection, and how many ticks to run this frame. Port of _loop / _get_ticks_in_loop in network-time.gd.

| | Member | Summary |
|---|---|---|
| property | `TickFactor` | 0.0 right after a tick, 1.0 right before the next one. |
| property | `WasPaused` | Set to true when the game was paused; the next Advance() skips catch-up ticks and re-syncs the tick. |
| method | `Advance(System.Double)` | Steps the simulation clock towards `referenceTime` and returns how many ticks to run now (0 or more). |
| method | `CompleteTick` | Call after each simulated tick. |
| method | `Reset(System.Double)` | Aligns the simulation clock with the reference clock, e.g. after the initial sync. |

## Netfox.Extras

### BaseNetInput

Base for input nodes: Gather() runs before every tick loop, only on the authority. Port of netfox.extras/base-net-input.gd.

| | Member | Summary |
|---|---|---|
| property | `Context` | The netfox stack this node uses; resolved when it enters the tree. |
| method | `Gather` | Read the local input devices into the input properties. |

### BodyStatePhysicsDriver

A driver that keeps the snapshots itself, as body states per Rid per tick. The Rapier drivers do not: the extension holds its own rolling cache, which is why the snapshot storage lives here and not in `PhysicsDriver`.

### GodotPhysicsDriver2D

2D counterpart of GodotPhysicsDriver3D. Port of netfox.extras/physics/godot_driver_2d.gd.

### GodotPhysicsDriver3D

Physics driver for Godot builds that expose manual stepping (PhysicsServer.space_step, godotengine/godot PR 76462). Snapshots every PhysicsBody per tick. Port of netfox.extras/physics/godot_driver_3d.gd.

### INetworkRigidBody

Physics bodies driven by a PhysicsDriver during rollback.

### NetworkRigidBody2D

RigidBody2D exposing its physics state as one synchronizable property. Port of netfox.extras/physics/network-rigid-body-2d.gd.

| | Member | Summary |
|---|---|---|
| property | `PhysicsState` | [origin, rotation, linear velocity, angular velocity, sleeping] |

### NetworkRigidBody3D

RigidBody3D exposing its physics state as one synchronizable property. Port of netfox.extras/physics/network-rigid-body-3d.gd.

| | Member | Summary |
|---|---|---|
| property | `PhysicsState` | [origin, rotation quaternion, linear velocity, angular velocity, sleeping] |

### NetworkSimulator

Editor convenience: the first launched instance hosts, later ones join, optionally through a UDP proxy that injects latency and packet loss. ENet only. Port of netfox.extras/network-simulator.gd.

| | Member | Summary |
|---|---|---|
| property | `Conditions` | Jitter and burst loss on top of `LatencyMs` and `PacketLossPercent`. |
| property | `ConnectPort` | The port to actually dial: the proxy's when it is running, the server's otherwise. |
| property | `HostPeerFactory` | Creates the peer to host with, or null when this instance could not take the host role - which is what makes the next one join instead. Upstream hardcodes `ENetMultiplayerPeer` (network-simulator.gd:70), so autoconnect is unusable with a Steam or loopback peer. Replace these before the simulator enters the tree to autoconnect over any transport. The UDP proxy only applies to ENet and is skipped for anything else, since it forwards real UDP packets. |
| property | `JoinPeerFactory` | Creates the peer to join with. See `HostPeerFactory`. |
| property | `Peer` | The peer the last autoconnect produced, or null if it never got one. |
| property | `ProxyCounts` | Packets the proxy passed through, and the two ways it did not. Zero bursts means no burst fired. |
| property | `ProxyPort` | Port clients connect to when the latency and loss proxy is in the way; otherwise `ServerPort`. |
| method | `Connect` | The autoconnect itself, without the editor and environment guards around it: host if nothing else has, join if something has. Assigns the resulting peer to the multiplayer API. |
| method | `InLossBurst(Netfox.Extras.NetworkSimulator.Profile,System.UInt64)` | Whether the link is in one of its outages. Derived from the clock rather than scheduled, so it needs no state and both directions go out together - which is what an outage is, as against loss on one path. |
| method | `OscillatingJitter(Netfox.Extras.NetworkSimulator.Profile,System.UInt64)` | A raised cosine over the period, so the delay drifts up and back down rather than jumping. |
| method | `ScheduleOrDrop(System.UInt64)` | When a packet entering the link now should come out the other end, or null if it never does. Both are decided here rather than on the way out: a packet lost on the wire was lost when it was sent, and a delay that is rolled per packet is what lets a later one arrive first. |
| method | `StartProxy(System.Int32,Netfox.Extras.NetworkSimulator.Profile,System.String)` | Starts the proxy with jitter and burst loss as well as the constant delay and even loss. `Realistic` is the one worth running against. |
| method | `StartProxy(System.Int32,System.Int32,System.Double,System.String)` | Starts only the latency and loss proxy, without the autoconnect flow that is limited to the editor, and returns the port clients should connect to. Upstream has no such entry point: its proxy is reachable only through autoconnect, which is why it was never covered by a headless run. |

### NetworkWeapon

Request/accept model for spawning projectiles: the client spawns immediately and asks the authority, which accepts, declines, or corrects. Not rollback based. Port of netfox.extras/weapon/network-weapon.gd.

| | Member | Summary |
|---|---|---|
| property | `Context` | The netfox stack this node uses; resolved when it enters the tree. |
| method | `CanFireImpl` | Whether the weapon can fire right now, e.g. based on cooldown. |
| method | `CanPeerUse(System.Int32)` | Whether `peerId` may use this weapon. Checked by the authority. |
| method | `Fire` | Spawn a projectile locally and request it from the authority. Returns null if the weapon cannot fire. |
| method | `GetData(Godot.Node)` | Data describing the projectile, sent to peers. |
| method | `IsReconcilable(Godot.Node,Godot.Collections.Dictionary,Godot.Collections.Dictionary)` | Whether the requesting peer and the authority agree closely enough to accept the projectile. |
| method | `Spawn` | Create the projectile node. |

### NetworkWeapon2D

NetworkWeapon for 2D: projectiles are reconciled by global transform. Port of netfox.extras/weapon/network-weapon-2d.gd.

| | Member | Summary |
|---|---|---|
| property | `DistanceThreshold` | Maximum distance between the requested and the authoritative spawn position to accept a projectile. |

### NetworkWeapon3D

NetworkWeapon for 3D: projectiles are reconciled by global transform. Port of netfox.extras/weapon/network-weapon-3d.gd.

| | Member | Summary |
|---|---|---|
| property | `DistanceThreshold` | Maximum distance between the requested and the authoritative spawn position to accept a projectile. |

### NetworkWeaponHitscan3D

Hitscan weapon: no projectile, the firing event is replicated and a raycast runs on every peer. Port of network-weapon-hitscan-3d.gd.

| | Member | Summary |
|---|---|---|
| property | `Exclude` | Bodies to exclude from the raycast. |
| method | `ApplyData(Godot.Collections.Dictionary)` | Reproduces the shot: casts the ray and calls OnHit / OnFire. |
| method | `GetData` | Data describing the shot: origin and forward direction. |
| method | `OnHit(Godot.Collections.Dictionary)` | Raycast hit: result has position, normal, collider. |

### NetworkWeaponProxy

NetworkWeapon that forwards its hooks to delegates, so Node2D/Node3D wrappers can host it. Port of network-weapon-proxy.gd.

### PhysicsDriver

Steps physics in time with netfox ticks and snapshots the physics space so it can take part in rollback. Subclasses bind to a concrete physics engine. Port of netfox.extras/physics/physics_driver.gd.

| | Member | Summary |
|---|---|---|
| property | `Active` | The driver stepping this process's space, for code that has to ask it something mid-tick - see `FlushQueries`. One per process in practice; a second one replaces it. |
| property | `Context` | The netfox stack this node uses; resolved when it enters the tree. |
| property | `PhysicsFactor` | Physics steps per network tick. |
| property | `RollbackPhysicsSpace` | Snapshot and roll back the entire physics space. |
| method | `AfterPrepareTick(System.Int32)` | Rolling the space back also moves every kinematic body in it to where the snapshot had it - but their nodes are restored by netfox from its own history, a moment later, and a node only pushes its transform to the body when the value changes. A player standing still against another was left with its body where the snapshot put it and its node where history did, and the other player walked into the node. And a push is applied on the next step, so a snapshot taken right after a tick has the kinematic bodies one tick behind their nodes. So on every resimulated tick, once history has been restored, every body that is not rolled back by state of its own is told where its node is. |
| method | `FlushQueries` | Makes every transform written since the last step visible to queries. Rapier applies a body's new transform on the next step, and `MoveAndSlide` is a query: the second player to move in a tick tests against where the first one was, and two players walking into each other pass through instead of stopping (measured: 3cm apart instead of 80). A kinematic body calls this after it has moved. Nothing to do on an engine that applies transforms as they are written. |
| method | `StepPhysics(System.Double,System.Int32)` | Steps physics for one tick, split into PhysicsFactor sub-steps, ticking NetworkRigidBody nodes in between. |
| method | `TrimSnapshots(System.Int32)` | Drops snapshots older than the rollback history. Drivers whose engine keeps its own cache do nothing. |

### RapierPhysicsDriver2D

2D counterpart of RapierPhysicsDriver3D. Port of netfox.extras/physics/rapier_driver_2d.gd.

### RapierPhysicsDriver3D

Physics driver for the Rapier GDExtension (appsinacup/godot-rapier-physics): manual space stepping plus its StateManager for whole-world snapshots. The extension has no C# bindings, so it is driven through ClassDB. Port of netfox.extras/physics/rapier_driver_3d.gd.

### RapierStateManager

The Rapier extension's StateManager2D/3D, driven through ClassDB. Snapshots are tagged with the tick they were taken for and found again by that tag, through `ordered_cache_tags`. Upstream's `.off` driver addressed the cache by age instead - "offset 0 is the newest" - behind a counter of stored states that nothing ever incremented, so it returned before loading anything, every time. This port reproduced that faithfully, and RapierCheck passed anyway because it never actually rolled back (netfox-net#62).

| | Member | Summary |
|---|---|---|
| method | `Rollback(Godot.Node,Godot.Rid,System.Int32)` | Restores the space to the snapshot taken for `tick`, and drops everything cached after it: the resimulation that follows takes those snapshots again, and a cache that kept both copies would hand back the stale one on the next rollback. Returns false when no snapshot for that tick exists. |

### RewindableRandomNumberGenerator

Random numbers that are reproducible per tick: the same tick always yields the same sequence, so resimulated ticks agree. Port of netfox.extras/rewindable-random-number-generator.gd.

### RewindableState

A state of a RewindableStateMachine. Override the virtual methods or subscribe to the events. Port of netfox.extras/state-machine/rewindable-state.gd.

| | Member | Summary |
|---|---|---|
| method | `CanEnter(Netfox.Extras.RewindableState)` | Return false to refuse the transition into this state. |
| event | `OnDisplayEnter` | (previous state, tick). Emitted once the state is shown on screen. |
| event | `OnDisplayExit` | (next state, tick). Emitted once the state is no longer shown on screen. |
| event | `OnEnter` | (previous state, tick, prevent). Call prevent to cancel the transition. |
| event | `OnExit` | (next state, tick, prevent). Call prevent to cancel the transition. |
| event | `OnTick` | (delta, tick, isFresh) |

### RewindableStateMachine

State machine whose current state is rollback state: list its State property on a sibling RollbackSynchronizer. Child RewindableState nodes are the available states. Port of netfox.extras/state-machine/rewindable-state-machine.gd.

| | Member | Summary |
|---|---|---|
| property | `Context` | The netfox stack this node uses; resolved when it enters the tree. |
| property | `State` | Name of the current state. Set it to jump without running transition callbacks; use Transition() otherwise. |
| method | `Transition(Godot.StringName)` | Attempt to transition; returns false if refused by CanEnter, OnExit or OnEnter handlers. |
| method | `UpdateStates` | Re-reads the child RewindableState nodes. |
| event | `OnDisplayStateChanged` | (old state, new state). Emitted after the tick loop when the shown state changed. |
| event | `OnStateChanged` | (old state, new state) |

### WindowTiler

Tiles the windows of game instances launched together from the editor. Port of netfox.extras/window-tiler.gd.

