# Visibility filters

By default netfox sends every replicated property to every peer. In a competitive game that is a problem: a client
that receives the position of a player it cannot see can draw them through a wall, and no amount of client-side
culling fixes it, because the data is already there.

A `PeerVisibilityFilter` decides, per peer, whether a node's state goes out at all.

## Getting at it

`RollbackSynchronizer` and `StateSynchronizer` both have one, added as a child automatically:

```csharp
var filter = synchronizer.VisibilityFilter;
```

> **Turn off input broadcast when you filter state.** With **Enable input broadcast** on, peers receive each other's
> input directly. Combined with filtered state that gives a peer input for a character it gets no state for, so it
> simulates that character from stale state forever. Filtering is a server-authoritative pattern; input broadcast is
> not.

## Three mechanisms, in order

**Filter callbacks** run first. Each takes a peer id and returns whether that peer may see the node. If any of them
says no, the answer is no:

```csharp
filter.AddVisibilityFilter(peer => _visibleTo.Contains(peer));
filter.AddVisibilityFilter(peer => LineOfSight(peer));

filter.RemoveVisibilityFilter(myFilter);   // keep the delegate if you want to remove it
filter.ClearVisibilityFilters();
```

Keep them cheap - they run for every peer, as often as the update mode says.

**Per-peer overrides** come next, and beat the default either way:

```csharp
filter.SetVisibilityFor(peer, true);    // always visible to this peer
filter.SetVisibilityFor(peer, false);   // never
filter.UnsetVisibilityFor(peer);        // back to the default
```

**Default visibility** is the fallback when nothing else has an opinion:

```csharp
filter.DefaultVisibility = false;       // hidden unless an override says otherwise
```

**Filters only subtract.** This is the one thing worth getting right. `GetVisibilityFor` returns false as soon as any
filter says false, and otherwise falls through to the override or the default - so a filter that returns `true` does
not grant visibility, it only declines to take it away. `DefaultVisibility = false` *plus a filter* is a node nobody
ever receives, and it looks like broken replication rather than like filtering.

So for fog of war, pick one of the two shapes:

```csharp
// Keep the default visible and let the callback carve peers out. This is what a distance or line of sight check is.
filter.DefaultVisibility = true;
filter.AddVisibilityFilter(peer => CanSee(peer));
```

```csharp
// Or start from nothing and hand visibility out per peer. Overrides are consulted after the filters, so these work.
filter.DefaultVisibility = false;
filter.SetVisibilityFor(peer, true);
```

## Update modes

The filter keeps a list of visible peers rather than re-deciding on every packet. `UpdateMode` says when that list
is rebuilt:

| Mode | When |
|---|---|
| `Never` | Only when you call `UpdateVisibility()` yourself. |
| `OnPeer` | When a peer joins or leaves. The default. |
| `PerTickLoop` | Before each tick loop - once per frame. |
| `PerTick` | Before each network tick. |
| `PerRollbackTick` | After each rollback tick. The most current and the most expensive. |

```csharp
filter.UpdateMode = PeerVisibilityFilter.UpdateModeEnum.PerTickLoop;
```

`OnPeer` is right for visibility that depends on who is in the game - teams, spectators. Anything that changes with
the world - line of sight, range - needs one of the per-tick modes, or you are sending stale decisions.

You can always force a rebuild:

```csharp
filter.UpdateVisibility();              // over the current peer list
filter.UpdateVisibility([2, 3, 4]);     // over an explicit one
```

## Reading the result

```csharp
filter.GetVisibilityFor(peer);   // the decision for one peer
filter.GetVisiblePeers();        // the current list
filter.GetRpcTargetPeers();      // the same, shaped for RpcId: a broadcast, one exclusion, or a list
```

`GetRpcTargetPeers` is there so your own RPCs can respect the same filtering as netfox's state - worth using, since
a game that hides positions but broadcasts "player fired" has not hidden much.
