# Network schemas

By default netfox serializes state and input through Godot's binary `Variant` encoding. That works for any value
without knowing anything about it in advance, and it is the reason a fresh project replicates anything at all - but
it pays for that generality in bytes, and it carries a type tag for every value.

You usually know more. A health value that never exceeds 100 does not need 64 bits. A direction vector is unit
length, so two of its three components are redundant. A schema is where you say so.

## Setting one

`RollbackSynchronizer` and `StateSynchronizer` both take a schema keyed by property path - the same paths as in
their state and input arrays:

```csharp
synchronizer.SetSchema(new Dictionary<string, NetworkSchemaSerializer>
{
    [":position"] = NetworkSchemas.Vec3F32(),
    [":velocity"] = NetworkSchemas.Vec3F16(),
    [":health"] = NetworkSchemas.Uint8(),
    ["Input:Movement"] = NetworkSchemas.Normal2F16(),
    ["Input:Jump"] = NetworkSchemas.Bool8(),
});
```

`SetSchema` replaces what is registered; `MergeSchema` adds to it, which is what to use when several scripts each
know about their own properties. `ClearSchema` goes back to `Variant` for everything.

Both ends have to agree. A schema is not negotiated over the network - it is part of the game, and a peer decoding
with a different one gets nonsense. Set it in `_Ready`, from code both peers run.

## Lossless and lossy

**Lossless** means the same information in fewer bytes. An inventory count capped at 99 as `Uint8()` instead of a
64-bit integer is a free win - the range simply fits. So is `Normal3F32()` for a unit vector: it reconstructs to the
same vector from fewer components.

**Lossy** means giving up precision on purpose. `Float16` for an NPC's velocity is invisible in play and costs half
of `Float32`. `Degrees8()` quantises an angle to about 1.4 degrees, which is fine for a body's facing and terrible
for a sniper's aim.

The judgement is per property and it is yours. The safe default is to leave everything on `Variant` until bandwidth
is actually a problem, then look at what is big and changes often - transforms, usually - before anything else.

## What is available

`NetworkSchemas` is a set of static factories:

| Group | Members |
|---|---|
| Generic | `Variant()`, `String()`, `CString()`, `Bool8()` |
| Unsigned | `Uint8/16/32/64()`, `Varuint()` |
| Signed | `Int8/16/32/64()` |
| Float | `Float16/32/64()` |
| Fraction | `Sfrac8/16/32()` for -1..1, `Ufrac8/16/32()` for 0..1 |
| Angle | `Degrees8/16/32()`, `Radians8/16/32()` |
| Vectors | `Vec2F16/32/64()`, `Vec3F…`, `Vec4F…` |
| Unit vectors | `Normal2F16/32/64()`, `Normal3F…` |
| Rotation | `QuatF16/32/64()` |
| Transforms | `Transform2F16/32/64()`, `Transform3F…` |
| Containers | `ArrayOf(item, size)`, `Dictionary(key, value, size)` |

The composite ones take a component serializer, so the precision is yours to pick:

```csharp
NetworkSchemas.Vec3T(NetworkSchemas.Sfrac16())        // a vector known to be in -1..1
NetworkSchemas.ArrayOf(NetworkSchemas.Uint8(), NetworkSchemas.Varuint())
```

`Varuint()` is worth knowing: a variable-length unsigned integer, one byte for small values. It is what netfox uses
for its own lengths, and it is usually the right choice for a count or an id.

## Measuring the difference

Schemas are an optimisation, so measure rather than guess. `NetworkPerformance` exposes per-tick counters - state
properties sent, full versus diffed - under Godot's monitors, and the harness case
`LoopbackHarnessTests.BandwidthAtPlayerScale` prints bytes per player per tick for a running session.

For reference, two players moving with the default `Variant` encoding and diff states on cost about 26 bytes per
player per tick of state, and input about 49 bytes per packet at redundancy 3.
