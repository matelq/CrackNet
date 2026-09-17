# Minimal examples

The smallest code for each thing a co-op game needs. Everything here compiles against the current API; the
playground (`examples/playground`) is the same, fleshed out. Every script below is an ordinary Godot node script:
nothing inherits from netfox, a `NetworkObject` child node does the networking. Every file starts with:

<!-- check: usings -->
```csharp
using Godot;
using Netfox;
```

## 1. Players

A scene `Player.tscn`, next to `Player.cs`:

```
Player (CharacterBody3D)      Player.cs
├── CollisionShape3D
├── MeshInstance3D
└── NetworkObject             Kind = Auto → Personal
```

<!-- check: file -->
```csharp
[Scene]                                        // spawnable; its scene is Player.tscn
public partial class Player : CharacterBody3D
{
    public override void _Ready() => AddToGroup("players");

    public override void _PhysicsProcess(double delta)
    {
        if (!this.Net().Authority.IsLocal) return;   // other peers only play this player back

        var input = Input.GetVector("left", "right", "forward", "back");
        Velocity = new Vector3(input.X * 6, Velocity.Y - 14 * (float)delta, input.Y * 6);
        MoveAndSlide();
    }
}
```

On the host, one player per peer:

<!-- check: file -->
```csharp
public partial class Game : Node3D
{
    [Export] public Node3D SpawnPoint { get; set; } = null!;

    // Call once this peer is hosting
    public void StartHosting()
    {
        Multiplayer.PeerConnected += id => Player.Spawn(SpawnPoint, authority: (int)id);
        Player.Spawn(SpawnPoint);
    }
}
```

`Player.Spawn` is generated for every `[Scene]` class: `Spawn(node)` places it where a node is, `Spawn(at: transform)`
at a global transform; `authority` is who simulates it, the caller by default.

What you get: each player's peer simulates its character with no input delay; its transform and velocity reach
everyone; a peer that joins later gets every player; a player whose peer leaves disappears everywhere.

## 2. Physics objects

```
Crate (RigidBody3D)
├── CollisionShape3D
├── MeshInstance3D
└── NetworkObject             Kind = Auto → Shared
```

No code. Place crates in the scene every peer loads, under the same names. To spawn them at run time, give the scene
a class and call its generated `Spawn`:

<!-- check: file -->
```csharp
[Scene]
public partial class Crate : RigidBody3D { }
```

<!-- check: members Player -->
```csharp
private void DropCrate() => Crate.Spawn(at: GlobalTransform.Translated(Vector3.Up * 3));
```

What you get: the host simulates them at first; each is frozen wherever another peer simulates it; a crate that hits
another passes its authority on, and one taken from under a stack takes the stack; a crate at rest goes back to the
host.

## 3. Players and objects

Walking into a crate takes it. To push it as well, set **Push Strength** on the player's `NetworkObject` in the
inspector (0.6 is a gentle shove): the library pushes the rigid bodies the character slides into, on this peer's
simulation. No code.

Claim, carry and throw:

<!-- check: members Player -->
```csharp
private RigidBody3D? _held;

private void GrabOrThrow(RigidBody3D nearest)
{
    if (_held is { } held)
    {
        held.Throw(-GlobalBasis.Z * 9 + Vector3.Up * 2);
        _held = null;
    }
    else if (nearest.TryClaim())
    {
        _held = nearest;
    }
}
```

<!-- check: body Player -->
```csharp
// In _PhysicsProcess: a claimed body is frozen, so it goes where it is put
if (_held is not null) _held.GlobalPosition = GlobalPosition - GlobalBasis.Z * 1.1f + Vector3.Up * 0.6f;
```

What you get: nobody else can take a claimed crate; a thrown one flies on the thrower's simulation and everyone sees
that flight.

## 4. Players and players

Put players on a collision layer their own mask leaves out: each peer would otherwise push a copy of the other player
from the past. A push is delivered to the pushed player's own peer, which adds it to its knockback:

<!-- check: members Player -->
```csharp
// The pusher, on its own peer
private void Shove(Player other) => this.Push(other, -GlobalBasis.Z * 4);
```

<!-- check: body Player -->
```csharp
// The pushed player, in _PhysicsProcess before MoveAndSlide
Velocity += this.Net().TakeKnockback(delta);
```

What you get: exactly one application of each push, on the peer that simulates the pushed player, wherever the push
came from.

## 5. Shooting

A scene `Shot.tscn`, next to `Shot.cs`:

```
Shot (Node3D)                 Shot.cs
├── MeshInstance3D
└── NetworkObject             Kind = Auto → Personal
```

<!-- check: file -->
```csharp
public partial class Shot : Node3D, ISpawnedWith<Vector3>
{
    private Vector3 _velocity;
    private double _age;

    // Spawn data: the same on every peer. Implementing ISpawnedWith makes the class spawnable too
    public void OnSpawned(Vector3 velocity) => _velocity = velocity;

    public override void _PhysicsProcess(double delta)
    {
        if (!this.Net().Authority.IsLocal) return;   // the shooter moves it and decides every hit
        GlobalPosition += _velocity * (float)delta;
        _age += delta;

        foreach (var player in GetTree().GetNodesInGroup("players").OfType<Player>())
        {
            if (player.Net().Authority.Peer == this.Net().Authority.Peer) continue;   // not the shooter
            if (player.GlobalPosition.DistanceTo(GlobalPosition) > 0.7f) continue;
            this.Push(player, _velocity.Normalized() * 6);
            this.Net().Despawn();                     // in the same decision: no second hit, no passing through
            return;
        }
        if (_age > 2.5) this.Net().Despawn();
    }
}
```

Firing, on the shooter's peer, from a `Marker3D` at the muzzle:

<!-- check: members Player -->
```csharp
[Export] public Marker3D Muzzle { get; set; } = null!;

private void Shoot() => Shot.Spawn(Muzzle, -Muzzle.GlobalBasis.Z * 18);
```

The velocity is required: `OnSpawned(Vector3 velocity)` has no default, so `Shot.Spawn(Muzzle)` does not build.

What you get: the shot appears at once for the shooter and from the muzzle for everyone else; the shooter's screen
decides what it hit; other peers see it vanish when their playback reaches the hit.

`this.Push(target, impulse)` is the same for a crate: it takes the crate for the shooter and it flies at once. For
hitscan, the shooter runs an ordinary ray query instead of moving a shot.
