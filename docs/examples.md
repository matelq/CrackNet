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

A scene `player.tscn`:

```
Player (CharacterBody3D)      Player.cs
├── CollisionShape3D
├── MeshInstance3D
└── NetworkObject             Kind = Auto → Personal
```

<!-- check: file -->
```csharp
public partial class Player : CharacterBody3D
{
    public NetworkObject Object => GetNode<NetworkObject>("NetworkObject");

    public override void _Ready() => AddToGroup("players");

    public override void _PhysicsProcess(double delta)
    {
        if (!Object.Authority.IsLocal) return;   // other peers only play this player back

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
    private static readonly PackedScene PlayerScene = GD.Load<PackedScene>("res://player.tscn");

    // Call once this peer is hosting
    public void StartHosting()
    {
        Multiplayer.PeerConnected += id => SpawnPlayer((int)id);
        SpawnPlayer(1);
    }

    private void SpawnPlayer(int peer)
        => NetworkObject.Spawn<Player>(GetNode("Players"), PlayerScene,
            player => player.Position = new Vector3(peer * 2, 1, 0), authority: peer);
}
```

What you get: each player's peer simulates its character with no input delay; its transform and velocity reach
everyone; a peer that joins later gets every player; a player whose peer leaves disappears everywhere.

## 2. Physics objects

```
Crate (RigidBody3D)
├── CollisionShape3D
├── MeshInstance3D
└── NetworkObject             Kind = Auto → Shared
```

No code. Place crates in the scene every peer loads, under the same names, or spawn them with `NetworkObject.Spawn`.

What you get: the host simulates them at first; each is frozen wherever another peer simulates it; a crate that hits
another passes its authority on, and one taken from under a stack takes the stack; a crate at rest goes back to the
host.

## 3. Players and objects

Walking into a crate already takes it. A character body does not push rigid bodies in Godot, so push it yourself,
after taking it so the push lands on this peer's simulation:

<!-- check: body Player -->
```csharp
// In Player._PhysicsProcess, after MoveAndSlide
for (var i = 0; i < GetSlideCollisionCount(); i++)
    if (GetSlideCollision(i).GetCollider() is RigidBody3D body && NetworkObject.Of(body) is { } crate
        && Object.Touch(crate))
        body.ApplyCentralImpulse(Velocity.Normalized() * 0.6f);
```

Grab, carry and throw:

<!-- check: members Player -->
```csharp
private RigidBody3D? _held;

private void GrabOrThrow(RigidBody3D nearest)
{
    if (_held is { } held)
    {
        NetworkObject.Of(held)!.Throw(-GlobalBasis.Z * 9 + Vector3.Up * 2);
        _held = null;
    }
    else if (NetworkObject.Of(nearest)!.TryGrab())
    {
        _held = nearest;
    }
}
```

<!-- check: body Player -->
```csharp
// In _PhysicsProcess: a held body is frozen, so it goes where it is put
if (_held is not null) _held.GlobalPosition = GlobalPosition - GlobalBasis.Z * 1.1f + Vector3.Up * 0.6f;
```

What you get: nobody else can take a held crate; a thrown one flies on the thrower's simulation and everyone sees
that flight.

## 4. Players and players

Put players on a collision layer their own mask leaves out: each peer would otherwise push a copy of the other player
from the past. A push is a knock, applied by the pushed player's own peer:

<!-- check: members Player -->
```csharp
// The pusher, on its own peer
private void Push(Player other) => other.Object.Knock(-GlobalBasis.Z * 4);

// The pushed player
private Vector3 _knockback;
```

<!-- check: body Player -->
```csharp
// In _Ready
Object.Knocked += impulse => _knockback += impulse;

// In _PhysicsProcess, before MoveAndSlide
Velocity += _knockback;
_knockback = _knockback.MoveToward(Vector3.Zero, 20 * (float)delta);
```

What you get: exactly one application of each push, on the peer that simulates the pushed player, wherever the push
came from.

## 5. Shooting

A scene `shot.tscn`:

```
Shot (Node3D)                 Shot.cs
├── MeshInstance3D
└── NetworkObject             Kind = Auto → Personal
```

<!-- check: file -->
```csharp
public partial class Shot : Node3D
{
    public Vector3 Velocity { get; set; }
    private NetworkObject Object => GetNode<NetworkObject>("NetworkObject");
    private double _age;

    public override void _PhysicsProcess(double delta)
    {
        if (!Object.Authority.IsLocal) return;   // the shooter moves it and decides every hit
        GlobalPosition += Velocity * (float)delta;
        _age += delta;

        foreach (var player in GetTree().GetNodesInGroup("players").OfType<Player>())
        {
            if (player.Object.Authority.Peer == Object.Authority.Peer) continue;   // not the shooter
            if (player.GlobalPosition.DistanceTo(GlobalPosition) > 0.7f) continue;
            player.Object.Knock(Velocity.Normalized() * 6);
            Object.Despawn();                     // in the same decision: no second hit, no passing through
            return;
        }
        if (_age > 2.5) Object.Despawn();
    }
}
```

Firing, on the shooter's peer:

<!-- check: members Player -->
```csharp
private static readonly PackedScene ShotScene = GD.Load<PackedScene>("res://shot.tscn");

private void Shoot()
    => NetworkObject.Spawn<Shot>(GetNode("../../Shots"), ShotScene, shot =>
    {
        shot.Position = GlobalPosition - GlobalBasis.Z * 0.8f;
        shot.Velocity = -GlobalBasis.Z * 18;
    });
```

What you get: the shot appears at once for the shooter and from the muzzle for everyone else; the shooter's screen
decides what it hit; other peers see it vanish when their playback reaches the hit.

To hit a crate as well, take it first and then knock it: `Object.Touch(crate); crate.Knock(impulse);`. For hitscan,
the shooter runs an ordinary ray query instead of moving a shot.
