# NetworkObject

One `NetworkObject` per replicated thing. It holds who simulates the object and who holds it, sends the `[Synced]`
state of its subtree while this peer is the authority, and plays that state back everywhere else. The reasoning behind
every rule here is in the [design document](design/distributed-authority.md).

## Authority and ownership

| Member | Meaning |
|---|---|
| `Authority`, `IsAuthority` | The peer that simulates the object and sends its state. Godot's multiplayer authority of the root. |
| `Holder` | The peer holding the object, or 0. A held object cannot be taken by anyone else. |
| `Transferable` | Whether other peers may take it at all. Off for player characters and projectiles. Replicated. |
| `AuthorityChanged` | Raised on every peer after authority or holder changed. Refresh freeze state and visuals here. |

Every change is optimistic: it applies on the requesting peer at once and goes to the host, which accepts it or
corrects the requester. Guests take the host's word. Each request returns false when it cannot even be tried (someone
else holds the object, it is not transferable, or this peer is not connected); true means applied here and sent, not
yet accepted.

| Call | When |
|---|---|
| `TryTakeAuthority()` | This peer interacts with a free object in a way that is not a contact from another object. |
| `Touch(other)` | An object this peer simulates made contact with `other`. Needs `SpreadsAuthority` on the caller. |
| `TryGrab()` | This peer picks the object up: authority and ownership. |
| `Release()` | Lets go. This peer keeps simulating it, so a throw flies on the thrower's machine. |
| `ReturnToHost()` | The object has come to rest; the host takes it back. |

Conflicts are settled by the host. A grab beats a touch, and of two touches the first one to arrive wins. A touch
carries its cause, so the counter-touch fails once its own cause has been taken. `MaxSpreadDepth` limits how many
objects a chain can pass authority through, counted from its source.

What counts as contact and as rest is up to the game. The rule to hold on to: **every interaction has exactly one
arbiter**, the authority of the object that started it. If two peers can both decide the same hit or grab, the
mechanic is not finished.

## Events: pushes and hits

```csharp
target.Object.SendToAuthority(impulse);                            // from anyone
Object.EventReceived += (fromPeer, payload) => _knockback += payload.AsVector3();   // on the target's authority
```

`SendToAuthority` delivers the payload reliably, exactly once, to whoever simulates the object. If authority moves
while the payload is on its way, the peer that no longer simulates the object passes it on. On the authority itself
it is raised at once - unless that authority is a claim the host has not confirmed yet: then it waits for the
host's answer, and goes to the winner if the claim lost.

Players do not collide with each other: each would be pushing a copy of the other in the past. A push is an event to
the pushed player's peer, applied as knockback there.

## Projectiles

A projectile belongs to its shooter. Spawn it through a `MultiplayerSpawner` whose authority is the shooter, with
`Transferable = false`. The shooter's peer moves it and decides every hit against the targets it displays: it sends
the effect with `SendToAuthority` and calls `Despawn()` in the same frame, so a projectile cannot pass through its
first target or hit twice. `PlaygroundShot` is the worked example.

To push a crate with a projectile, `Touch` the crate first and then send the impulse. The touch takes the crate for
the shooter, and the event reaches whoever ends up simulating it.

## Despawn and teleport

- `Despawn()` ends the object's timeline. The authority hides it and stops processing at once. Other peers keep
  showing it until their playback reaches the final sample, then hide it; the node is freed after a grace period. Do
  not `QueueFree` a replicated object yourself.
- `Teleport()` makes the next sample apply without blending: a respawn, not a flight across the map.

## Playback

A remote object is shown from its authority's samples, a little in the past:

- **One clock per remote peer.** Everything one peer sends is shown at the same tick, so a player and the crate it
  carries never drift apart.
- **An adaptive buffer per link.** Depth is the send interval plus that link's measured jitter, capped at 20 ticks.
  The buffer absorbs jitter; it cannot absorb latency, because nothing can be shown before it arrives.
- **No freezing, no rewriting.** On underrun the object holds its last value; after an outage playback catches up
  quickly. A late sample never rewrites what was already shown.
- **Rest costs almost nothing.** An unchanged object is sent once a second as a heartbeat, and starts moving on
  observers exactly when it did on its authority.
- **Objects start at their first sample.** A spawned object is hidden until playback reaches its first sample. A
  projectile starts at the muzzle, not hanging there or appearing down range.

`NetworkObjectServer.Instance.GetPlaybackStatus(peer)` reports, averaged over a second, how old that peer's state is on
arrival and how long it waits in the buffer. `DisplayTick`, `SampleSent` and `SampleReceived` are there for checks
and diagnostics.

State goes out every `NetworkObjectServer.StateIntervalTicks` ticks (15 Hz at the default 30 Hz tick).
