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

### CommandIds

Well-known command ids. Explicit so they do not depend on autoload order.

| | Member | Summary |
|---|---|---|
| field | `FirstUserCommand` | First id handed out by RegisterCommand for user commands. |

### CompactValues

Synced values on the wire: a type byte, then the value at float precision. `GD.VarToBytes` costs a 4-byte header per value on top of doubles; a Vector3 goes from 20 bytes to 13. Types without a case here fall back to it.

### DenseSnapshotSerializer

Full state: every registered property of every auth subject. Port of serializers/dense-snapshot-serializer.gd.

### ISyncedProperties

Declares the synced properties of a node. Implemented by the source generator for every partial type with `[Synced]` properties; it can also be implemented by hand.

### Interpolators

Registry of value interpolators by type. Discrete types (bool, integers, integer vectors, strings, references) have none on purpose: they step.

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
| property | `SimulatedProfile` | A named NetworkSimulator profile, or "Custom" for the latency, loss, jitter and burst settings. |
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

### NetworkObject

One replicated object. While its root is this peer's multiplayer authority it sends the `[Synced]` properties of its subtree every tick; otherwise it plays them back from the authority's samples, a few ticks behind, on the clock shared by everything that peer sends. See docs/design/distributed-authority.md. Authority moves at runtime: `TryTakeAuthority` when this peer touches the object, `TryGrab` when it holds it, `Release` and `ReturnToHost` after. Every change applies here at once and goes to the host, which accepts it or corrects this peer. A change is newer when its ownership sequence is higher, or equal with a higher authority sequence, so a grab beats a touch. A nested `NetworkObject` owns its own subtree: a crate carried inside a player is not part of the player.

| | Member | Summary |
|---|---|---|
| property | `Context` | The stack this object belongs to; resolved when it enters the tree. |
| property | `Holder` | The peer holding the object, or 0 when nobody does. |
| property | `IsAuthority` | True when this peer simulates the object and sends its state. |
| property | `LastSentBody` | What this peer last sent for the object, and when: an unchanged object is not sent again for a while. |
| property | `Root` | The node that is the object: authority, identity and the synced subtree. The parent by default. |
| property | `Transferable` | Whether other peers may take authority or ownership. Off for players: their character stays theirs. |
| method | `Despawn` | Ends this authoritative object's timeline. It is hidden and stops processing here immediately; remote peers hide it when their playback reaches the flagged final sample, and the root is freed after the playback grace period so a `MultiplayerSpawner` cannot remove it from observers early. |
| method | `IsNewer(System.Int32,System.Int32)` | True when ( `ownershipSequence`, `authoritySequence`) is newer than what this object has. |
| method | `Release` | Lets go of a held object. This peer keeps simulating it until someone else touches it. |
| method | `ReturnToHost` | Hands a free object this peer simulates back to the host, typically once it has come to rest. |
| method | `SendToAuthority(Godot.Variant)` | Delivers `payload` to whoever is this object's authority, reliably and exactly once, even if authority moves while it is on its way: the transport does not duplicate, and a peer that is no longer the authority passes the event on instead of raising it. On the authority itself it is raised at once. |
| method | `Teleport` | The next state this peer sends applies without interpolation on the others: a respawn, not a flight. |
| method | `TryGrab` | Takes ownership and authority. False when someone else holds it. |
| method | `TryTakeAuthority` | Takes authority over a free object this peer touched. False when it is held by someone else, or when this peer is not connected; true means applied here and sent, not yet accepted by the host. |
| event | `AuthorityChanged` | Raised after the authority or the owner changed, on every peer. |
| event | `EventReceived` | Raised on the authority, exactly once per `Variant` call anywhere: the peer that sent it and what it sent. A push, damage, "this projectile hit me". |
| event | `SampleReceived` | Raised on a peer playing the object back, for every sample of the authority's state it kept: the tick it is for. What arrived, as against what was sent - for diagnostics and checks. |
| event | `SampleSent` | Raised on the authority for every state it sends: the tick. Not every tick - an unchanged object is sent only as a heartbeat - so this is the ground truth a check compares playback against. |

### NetworkObjectServer

Sends the state of every `NetworkObject` this peer is authority for, once per tick, and plays back the state of every other one. Keeps one `PlaybackClock` per remote peer. A packet is the tick, then one block per object: its identity reference, a length so a receiver that does not know the object yet can skip it, a teleport flag and the values in property order. Packets are unreliable and split between objects to stay under the packet size limit. A lost packet is not resent: the next tick supersedes it.

| | Member | Summary |
|---|---|---|
| property | `Context` | The stack this server belongs to; resolved when it enters the tree. |
| property | `PlaybackDelayTicks` | How many ticks behind the newest sample remote objects are shown. |
| field | `RestHeartbeatTicks` | An object whose state has not changed is sent again only this often. |
| field | `StateIntervalTicks` | State goes out every this many ticks: 2 at 30 Hz is 15 snapshots a second. |
| field | `_pendingAuthority` | What the host said about objects this guest does not have yet. A late joiner hears about every object as it connects, and MultiplayerSpawner delivers spawned ones on its own channel, in no particular order with ours. |
| method | `ErasePeer(System.Int32)` | Forgets a peer's clock. On the host, also takes back every object the peer simulated or held and tells everyone: otherwise a crate carried out of the session stays with nobody for good. |
| method | `GetDisplayTick(System.Int32)` | The display tick for objects of `peer`, or null before anything arrived from it. |
| method | `GetPlaybackStatus(System.Int32)` | The receive and playback positions for `peer`, or null before any state arrived. The caller can compare `NewestTick` with its local network tick for network age, and with `DisplayTick` for buffered playback age. |
| method | `HandleAuthority(System.Int32,System.Byte[])` | On the host: accepts a guest's change when it is newer and the object is free or already the guest's, and tells everyone; otherwise tells the guest what stands. On a guest: whatever the host says stands. |
| method | `HandleEvent(System.Int32,System.Byte[])` | Raises an event on its object if this peer is the authority, and passes it on to the authority otherwise. |
| method | `SendAllAuthorityTo(System.Int32)` | On the host: tells a peer that just joined who has authority over and who holds every object. |
| method | `SubmitAuthority(Netfox.NetworkObject)` | Sends an authority change this peer just applied: a guest asks the host, the host tells everyone. |

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

### PlaybackStatus

The newest state tick received from a peer and the tick currently displayed for that peer.

| | Member | Summary |
|---|---|---|
| method | `#ctor(System.Int32,System.Double)` | The newest state tick received from a peer and the tick currently displayed for that peer. |

### PropertyEntry

Parses "Node/Path:property" strings relative to a root node. Port of properties/property-entry.gd.

| | Member | Summary |
|---|---|---|
| method | `MakePath(Godot.Node,System.Object,System.String)` | Builds a "node:property" path string. Node may be a string, NodePath, or Node relative to `root`. |
| method | `Parse(Godot.Node,System.String)` | The part before the colon is a node path relative to `root`, the rest is the property. |

### RedundantSnapshotSerializer

Packs several snapshots into one packet, for input redundancy. Port of serializers/redundant-snapshot-serializer.gd. The first snapshot goes in full and the rest only as how they differ from it, which is what upstream's TODO(#560) asks for: consecutive ticks of input are mostly identical, so a redundant copy is usually a few bytes of header. Every subject the older snapshot has still gets a frame, empty when nothing changed, so the reader can tell an unchanged subject from one that was not in that tick at all.

### SparseSnapshotSerializer

Diff state: only properties present in the snapshot, flagged by a bitset. Port of serializers/sparse-snapshot-serializer.gd.

### SyncedAttribute

Marks a property as state its NetworkObject sends while authoritative and plays back otherwise. The declaring type has to be partial; the generator implements `ISyncedProperties` on it. Properties only: Godot exposes a partial class's properties to Get and Set, but not its plain fields.

| | Member | Summary |
|---|---|---|
| property | `Interpolate` | Blend between samples on playback. Types with no in-between (bool, int, enums, strings, references) step regardless; set false on a continuous value that should step too. |

### SyncedProperty

A property a NetworkObject replicates: its path relative to the declaring node, and whether playback blends it.

| | Member | Summary |
|---|---|---|
| method | `#ctor(System.String,System.Boolean)` | A property a NetworkObject replicates: its path relative to the declaring node, and whether playback blends it. |

## Netfox.Core.Collections

### Bitset

Stores a list of booleans packed into bytes. Port of netfox.internals/bitset.gd.

### HistoryBuffer`1

Maps ticks to arbitrary data, stored in a sliding ring buffer. Port of netfox.internals/history-buffer.gd.

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

### ObjectPlaybackCursor

A newly seen object's display position. It begins at that object's first sample even when its peer's shared clock is already ahead, then runs faster until it can rejoin the shared clock without skipping its opening motion.

| | Member | Summary |
|---|---|---|
| property | `IsCatchingUp` | Whether this object is still behind the peer's shared display clock. |
| method | `Advance(System.Double,System.Double)` | Advances the private opening timeline, returning to the shared tick as soon as it reaches it. |
| method | `Start(System.Int32,System.Double)` | Starts at the first sample, or at the shared clock when that sample is not in its past. |

### PlaybackClock

The display tick for everything one remote peer sends. One clock per peer, not per object, so a stack of crates or a character and what it holds are always shown at the same moment. The clock trails the newest tick heard from the peer by a fixed delay and slews its rate to hold that depth, rather than jumping when packet spacing changes. The display never runs past the newest tick, but the clock's own time keeps going while nothing arrives: a resting peer sends only heartbeats, and motion after a rest must show at the normal depth at once, not a heartbeat late. After an outage that means a skip forward to where the data is, as Source does, rather than seconds of added delay draining at a few percent. It never runs backwards.

| | Member | Summary |
|---|---|---|
| property | `Holds` | Advances where the clock started holding at the newest tick; a measure of underruns. |
| property | `Newest` | The newest tick heard from the peer. |
| property | `Tick` | The tick to display, or null until the peer has sent anything. |
| method | `Advance(System.Double)` | Moves the display tick on by `elapsedTicks` of local time. |
| method | `Observe(System.Int32)` | Tells the clock a sample for `tick` arrived from the peer. |

### SampleTrack`1

Tick-stamped samples of one object, read at a display tick from its peer's `PlaybackClock`. Holds the newest sample once the display tick passes it. Values must be immutable after insertion.

| | Member | Summary |
|---|---|---|
| method | `Push(System.Int32,`0,System.Nullable{System.Double})` | Adds a sample unless it lands before `shownTick`: a late packet inside the interval already on screen would bend the viewer's past. Returns whether it was kept. |
| method | `TryGetNewest(System.Int32@,`0@)` | The newest sample, if any. |
| method | `TrySample(System.Double,`0@,`0@,System.Double@)` | The samples around `tick` and how far between them it is; false before the first sample, and both the newest after the last. Drops samples that no later tick can need. |

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

### NetworkSimulator

Editor convenience: the first launched instance hosts and later ones join. Link conditions are applied in process by `SimulatedMultiplayerPeer`, once as each packet leaves for a remote peer.

| | Member | Summary |
|---|---|---|
| property | `Conditions` | Conditions applied by the in-process wrapper. |
| property | `HostPeerFactory` | Creates the peer used to elect the first editor instance as host. |
| property | `JoinPeerFactory` | Creates the peer used when the hosting port was already taken. |
| property | `Peer` | The peer produced by autoconnect, wrapped with this instance's link conditions. |
| method | `Connect` | Hosts if the configured port is free, otherwise joins, then installs the simulated peer. |
| method | `InLossBurst(Netfox.Extras.NetworkSimulator.Profile,System.UInt64)` | Whether an unreliable packet sent now falls inside a periodic link outage. |
| method | `OscillatingJitter(Netfox.Extras.NetworkSimulator.Profile,System.UInt64)` | A raised cosine over the period, so delay drifts instead of jumping. |

### SimulatedMultiplayerPeer

A transport wrapper that applies a `Profile` once, on the sending side of each remote link. Reliable packets are delayed but never lost; unreliable packets also see the profile's steady and burst loss. The wrapped transport still owns connection establishment and packet delivery.

### WindowTiler

Tiles the windows of game instances launched together from the editor (Debug > Customize Run Instances), on Project Settings > Netfox > Extras > Auto Tile Windows. Every instance keeps a lock file in the cache directory fresh twice a second; a lock that has not been touched for a few seconds belongs to an instance that is gone. Each instance lays itself out again whenever the set of live locks changes. The original decided once, in its first two seconds, and deleted every lock older than three: instances that take several seconds each to start - C# and a physics extension - each saw only themselves and all maximised on top of each other.

| | Member | Summary |
|---|---|---|
| method | `LiveLocks` | The uids of instances that touched their lock recently, oldest first; stale locks are removed. |

