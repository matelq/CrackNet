# Getting started

From nothing to a player and a crate replicated between peers. `examples/playground` is the finished version of the
same thing: keep it open alongside.

## Installing

Drop `addons/netfox-net` into your project and enable the plugin in **Project Settings > Plugins**. That registers the
autoloads (`NetworkTime`, `NetworkEvents`, the servers behind `NetworkObject`) in the order they need, and the project
settings under **Netfox**.

The addon compiles into your game's own assembly, so the project needs `ImplicitUsings` and `Nullable`. `[Synced]`
comes from the source generator in the release zip:

```xml
<PropertyGroup>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
</PropertyGroup>
<ItemGroup>
  <Analyzer Include="addons/netfox-net/analyzers/Netfox.SourceGenerators.dll" />
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
lobby member, and `examples/playground/PlaygroundMesh.cs` builds the same over ENet.

## A player

A player's character is always simulated by its own peer: input applies at once, with no prediction.

```csharp
public partial class Player : CharacterBody3D
{
    [Synced] public Vector3 NetPosition { get => GlobalPosition; set => GlobalPosition = value; }
    [Synced] public float NetYaw { get => Rotation.Y; set => Rotation = new Vector3(0, value, 0); }

    public NetworkObject Object { get; private set; } = null!;

    public static Player Create(int peer)
    {
        var player = new Player { Name = $"Player{peer}" };
        player.SetMultiplayerAuthority(peer);
        player.Object = new NetworkObject { Name = "NetworkObject", Transferable = false };
        player.AddChild(player.Object);
        return player;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!Object.IsAuthority) return;   // everyone else plays back what this peer sends
        // read input, MoveAndSlide
    }
}
```

What makes it work:

- **`[Synced]` marks state.** The type has to be `partial`, and it has to be a property: Godot does not expose plain
  fields to `Get` and `Set`. Continuous values blend between samples; `[Synced(Interpolate = false)]` makes one step.
  Discrete types (bool, int, enums, strings, references) always step.
- **`NetworkObject` sends and plays back its subtree.** It gathers `[Synced]` properties from its parent and the
  parent's descendants, stopping at any nested `NetworkObject`. While this peer is the authority it sends them; on
  every other peer it writes them from playback.
- **The node's multiplayer authority is the object's authority.** Set it before the node enters the tree, as a
  `MultiplayerSpawner`'s spawn function does. `Transferable = false` keeps it there.

Spawn players with a `MultiplayerSpawner` on the host, so late joiners get them too. The host also sends a late joiner
who holds and simulates every object.

## A crate

A crate starts on the host and moves to whoever touches it.

```csharp
public partial class Crate : RigidBody3D
{
    [Synced] public Transform3D NetTransform { get => GlobalTransform; set => GlobalTransform = value; }
    [Synced] public Vector3 NetLinearVelocity { get => LinearVelocity; set => LinearVelocity = value; }
    [Synced] public Vector3 NetAngularVelocity { get => AngularVelocity; set => AngularVelocity = value; }

    public NetworkObject Object { get; private set; } = null!;

    public override void _Ready()
    {
        FreezeMode = FreezeModeEnum.Kinematic;
        Object.AuthorityChanged += Refresh;
        Refresh();
        BodyEntered += other =>
        {
            if (other is Crate crate && Object.IsAuthority) Object.Touch(crate.Object);
        };
    }

    private void Refresh() => Freeze = !Object.IsAuthority || Object.Holder != 0;
}
```

- **Only the authority simulates it.** Everywhere else it is kinematic and follows playback; running physics on top
  of received state is how two peers end up disagreeing.
- **Contact passes authority.** The player's `NetworkObject` and the crate's both have `SpreadsAuthority` on. The
  player calls `Object.Touch(crate.Object)` when it walks into a crate, and the crate does the same for what it knocks
  over, so a whole pile follows the player who pushed it.
- **Rest returns it.** After the crate has been still for a while, its authority calls `Object.ReturnToHost()`.

With Rapier, set the transform again after switching `Freeze`: Rapier returns a body to its last kinematic target on
the next freeze. `PlaygroundCrate.SetFrozen` shows how.

Next: **[NetworkObject](network-object.md)** for grabbing, events and projectiles.
