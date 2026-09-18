# Getting started

From nothing to a player and a crate replicated between peers. `examples/playground` is the finished version of the
same thing: keep it open alongside.

## Installing

Drop `addons/netfox-net` into your project and enable the plugin in **Project Settings > Plugins**. That registers the
autoloads (`NetworkTime`, `NetworkEvents`, the servers behind `NetworkObject`) in the order they need, and the project
settings under **Netfox**.

The addon compiles into your game's own assembly, so the project needs `ImplicitUsings` and `Nullable`. `[Synced]` and
the generated `Spawn` come from the source generator in the release zip, which reads your scenes to check that every
spawnable class has one:

```xml
<PropertyGroup>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
</PropertyGroup>
<ItemGroup>
  <Analyzer Include="addons/netfox-net/analyzers/Netfox.SourceGenerators.dll" />
  <AdditionalFiles Include="**/*.tscn" Exclude=".godot/**" />
</ItemGroup>
```

## Connecting

Assign any `MultiplayerPeer`. `NetworkEvents` starts the tick clock on the host at once and on a client once it is
connected and synchronized; you do not start it yourself.

```csharp
var peer = new ENetMultiplayerPeer();
peer.CreateServer(9999);          // or CreateClient(address, 9999)
Multiplayer.MultiplayerPeer = peer;
```

Guests send state straight to each other, so the intended transport is a full mesh: `SteamMultiplayerPeer` joins every
lobby member, and `Netfox.Extras.EnetMesh` builds the same over ENet for local windows and a LAN.

## A crate

```
Crate (RigidBody3D)
├── CollisionShape3D
├── MeshInstance3D
└── NetworkObject
```

That is the whole crate. `NetworkObject` reads its parent's type: a rigid body is **Shared**, so the library

- sends its transform and both velocities while this peer simulates it, and plays them back everywhere else;
- freezes it wherever another peer simulates it, so no second physics runs on top of received state;
- passes authority to whatever it hits while moving, and takes the bodies resting on and against it when it is taken,
  so a whole pile follows the player who pushed it;
- hands it back to the host once it has been at rest for half a second.

Put the crate in the scene of every peer under the same name, or spawn it (below). It starts with the host.

## A player

```
Player (CharacterBody3D)
├── CollisionShape3D
├── MeshInstance3D
└── NetworkObject
```

```csharp
public partial class Player : CharacterBody3D
{
    [Synced] public int Health { get; set; }

    public override void _PhysicsProcess(double delta)
    {
        if (!this.Authority.IsLocal) return;   // everyone else plays back what this peer sends
        Velocity += this.TakeImpulses(delta);  // pushes from other players
        // read input, MoveAndSlide
    }
}
```

A character body is **Personal**: it stays with its own peer, input applies at once with no prediction, and whatever
it slides into (a crate) is taken by that peer. Its transform and velocity are sent without being marked;
`[Synced]` is for the rest of its state. The type has to be `partial`, and synced state has to be a property.
Continuous values blend between samples; `[Synced(Interpolate = false)]` makes one step, and discrete types always
step.

The node's multiplayer authority is the object's authority. Set it before the node enters the tree - the generated
`Spawn` does.

## Spawning

A class with `[Scene]`, or one implementing `ISpawnedWith<T>`, gets a generated `Spawn`. Its scene is the one named after
the class next to its script (`Shot.cs`, `Shot.tscn`), or the path given to `[Scene("res://...")]`.

```csharp
[Scene]
public partial class Crate : RigidBody3D { }

public partial class Shot : Node3D, ISpawnedWith<Vector3>
{
    public void OnSpawned(Vector3 velocity) { /* the same on every peer */ }
}

Crate.Spawn(at: transform);                          // at a global transform
Shot.Spawn(Muzzle, -Muzzle.GlobalBasis.Z * 18);      // where a node is, with its spawn data
Player.Spawn(SpawnPoint, authority: peer);           // the host spawns a character another peer simulates
```

The scene appears on every peer and on late joiners, simulated by the caller unless `authority:` says otherwise, under
the current scene unless `parent:` does. `OnSpawned` runs everywhere before the object enters the tree, so data the
host sets for another peer's object reaches that peer too. Spawn data travels in a Godot `Variant`; it is required
unless `OnSpawned` declares a default value. A missing scene, a class at the root of two scenes, or spawn data a
`Variant` cannot hold fails the build.

Freeing an object on its authority frees it everywhere; call `Despawn()` rather than `QueueFree` so observers see it
to the end first.

## What is sent

Shown read-only on every `NetworkObject` in the inspector:

| Root | Sent |
|---|---|
| `RigidBody3D` | transform, linear and angular velocity, then `[Synced]` |
| `CharacterBody3D` | transform, velocity, then `[Synced]` |
| other `Node3D` | transform, then `[Synced]` |
| `Node`, `Control` | `[Synced]` only |
| any `Node2D` | not supported: netfox-net is 3D only |

Nothing else: child transforms, animation and particles only when marked `[Synced]`. Changed state goes out at the
next send (15 times a second), unchanged state once a second. `SoftBody3D` and ragdoll bones are not supported: the
node shows an error and the game quits at start.

Next: **[NetworkObject](network-object.md)** for kinds, grabbing, knocks and events.
