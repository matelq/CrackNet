# Getting started

Building a rollback-networked character from nothing. The end result is `examples/playground` - open it alongside
this, it is the same thing finished.

## Installing

Drop `addons/netfox-net` into your project and enable the plugin in **Project Settings > Plugins**. That registers the
autoloads (`NetworkTime`, `NetworkRollback` and the servers behind them) in the order they need, and the project
settings under **Netfox**.

The addon compiles into your game's own assembly - Godot does not attach node scripts from external assemblies - so
your project needs `ImplicitUsings` and `Nullable` enabled:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
</PropertyGroup>
```

The release zip also carries `addons/netfox-net/analyzers/Netfox.SourceGenerators.dll`. It is optional - it provides
the `[RollbackState]`-style attributes - and reference it as an analyzer if you want them:

```xml
<ItemGroup>
  <Analyzer Include="addons/netfox-net/analyzers/Netfox.SourceGenerators.dll" />
</ItemGroup>
```

## The shape of a rollback character

```
Player                        CharacterBody3D, your movement code
  CollisionShape3D
  MeshInstance3D
  Input                       BaseNetInput, what the player pressed
  RollbackSynchronizer        which properties are state, which are input
  TickInterpolator            smooths the display between ticks
```

Two nodes and an authority split are the whole idea: the **host** owns the character and simulates it, each **peer**
owns its own input node and sends it in.

## Input

Everything the player decides for one tick, and nothing else:

```csharp
using Godot;
using Netfox.Extras;

[GlobalClass]
public partial class PlayerInput : BaseNetInput
{
    [RollbackInput] public Vector2 Movement { get; set; }
    [RollbackInput] public bool Jump { get; set; }

    protected override void Gather()
    {
        Movement = Godot.Input.GetVector("ui_left", "ui_right", "ui_up", "ui_down");
        Jump = Godot.Input.IsActionPressed("ui_accept");
    }
}
```

`Gather` runs before each tick loop and only on the peer that owns the node. netfox records what it leaves here and
sends it to whoever simulates the character.

Without the attributes, implement `IRollbackInputProperties` and return the names - or type the paths into the
synchronizer's inspector. The attributes just make a rename carry the path along.

## The character

Movement goes in `RollbackTick`, not `_PhysicsProcess`:

```csharp
using Godot;
using Netfox;

[GlobalClass]
public partial class PlayerCharacter : CharacterBody3D, IRollbackTick
{
    [Export] public float Speed { get; set; } = 5.0f;

    [RollbackState] public int JumpsLeft { get; set; }
    [RollbackState] public bool JumpHeld { get; set; }

    private PlayerInput _input = null!;

    public override void _Ready() => _input = GetNode<PlayerInput>("Input");

    public void RollbackTick(double delta, int tick, bool isFresh)
    {
        var velocity = Velocity;
        velocity.X = _input.Movement.X * Speed;
        velocity.Z = _input.Movement.Y * Speed;

        // MoveAndSlide assumes the delta of the frame it is called from, not the tick delta
        var factor = (float)NetworkTime.Instance.PhysicsFactor;
        Velocity = velocity * factor;
        MoveAndSlide();
        Velocity /= factor;
    }
}
```

`RollbackTick` may run several times for the same tick, frames apart. It has to be a function of the state it is
given and the input for `tick`, and of nothing else. The [caveats](rollback-caveats.md) are worth reading before you
write much more than this.

## Wiring the synchronizer

In the inspector, on the `RollbackSynchronizer`:

- **Root**: the `Player` node.
- **State properties**: `:position` and `:velocity`. They belong to `CharacterBody3D`, so there is nowhere to put an
  attribute and they go in as strings. `JumpsLeft` and `JumpHeld` appear here too once the scene is saved - that is
  the attributes being gathered.
- **Input properties**: filled in by the same gather.
- **Enable prediction**: on, so other players keep moving while their input is in flight.

On the `TickInterpolator`, set **Root** to the player and **Properties** to `:position`. Rollback runs at the
tickrate; the interpolator is what stops that being visible.

## Connecting

```csharp
public override void _Ready()
{
    NetworkEvents.Instance.OnServerStart += () => SpawnPlayer(1);
    NetworkEvents.Instance.OnClientStart += SpawnPlayer;
    NetworkEvents.Instance.OnPeerJoin += SpawnPlayer;
    NetworkEvents.Instance.OnPeerLeave += DespawnPlayer;
}

private void Host()
{
    var peer = new ENetMultiplayerPeer();
    if (peer.CreateServer(9999, 8) != Error.Ok) return;
    Multiplayer.MultiplayerPeer = peer;
}

private void Join(string address)
{
    var peer = new ENetMultiplayerPeer();
    if (peer.CreateClient(address, 9999) != Error.Ok) return;
    Multiplayer.MultiplayerPeer = peer;
}
```

`NetworkEvents` starts and stops the tick loop with the session, so you never call `NetworkTime.Start` yourself.

Spawning has one rule that is easy to miss: **the same node must have the same name on every peer**. netfox addresses
nodes by their path, so a character the host calls `Player_2` has to be `Player_2` everywhere.

```csharp
private void SpawnPlayer(int peer)
{
    var player = PlayerScene.Instantiate<Node3D>();
    player.Name = $"Player_{peer}";

    player.SetMultiplayerAuthority(1);                              // the host owns the character
    player.GetNode("Input").SetMultiplayerAuthority(peer);          // the player owns their input

    SpawnRoot.AddChild(player);
}
```

## Trying it under latency

Two instances on one machine both feel perfect, which teaches you nothing. Turn on **Project Settings > Netfox >
Autoconnect** with simulated latency and packet loss, put a `NetworkSimulator` node in your main scene, and the
editor's extra instances connect to each other through a local proxy that delays and drops packets.

That is where you find out whether your `RollbackTick` really is a function of state and input - and what your
display offset should be.

## Where to go next

- [Rollback caveats](rollback-caveats.md) - read before debugging anything.
- [Prediction](prediction.md) - when reusing the last input is not good enough.
- [Migration notes](migration.md) - if you know the GDScript original.
- `examples/playground` - the finished version of everything above.
