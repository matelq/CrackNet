using Godot;

namespace Netfox.Tests;

/// <summary>A replicated body for harness cases: while authoritative it moves along <see cref="Velocity"/> every tick.</summary>
public partial class HarnessBody : Node3D
{
    [Synced] public Vector3 Location { get; set; }
    [Synced] public int Ticks { get; set; }

    public Vector3 Velocity { get; set; }

    /// <summary>Off for a body that should be truly at rest: counting ticks is a change every tick.</summary>
    public bool CountsTicks { get; set; } = true;
    public NetworkObject Object { get; private set; } = null!;

    private NetworkTime _time = null!;

    /// <summary>Adds a body named <paramref name="name"/> under <paramref name="stack"/>, owned by <paramref name="authority"/>.</summary>
    public static HarnessBody Spawn(Node stack, string name, int authority, Vector3 velocity = default, Vector3 location = default)
    {
        var body = Create(name, authority, velocity, location);
        stack.AddChild(body);
        return body;
    }

    /// <summary>A body not yet in the tree, for a MultiplayerSpawner's spawn function to return.</summary>
    public static HarnessBody Create(string name, int authority, Vector3 velocity = default, Vector3 location = default)
    {
        var body = new HarnessBody { Name = name, Velocity = velocity, Location = location };
        body.SetMultiplayerAuthority(authority);
        body.Object = new NetworkObject { Name = "NetworkObject", Kind = NetworkObject.ObjectKind.Custom };
        body.AddChild(body.Object);
        return body;
    }

    /// <summary>The scene <see cref="NetworkObject.Spawn{T}"/> instances in harness cases.</summary>
    public static PackedScene Scene => GD.Load<PackedScene>("res://test/Harness/harness_body.tscn");

    public override void _EnterTree()
    {
        // Instanced from the scene rather than built by Create
        if (GetNodeOrNull<NetworkObject>("NetworkObject") is { } existing)
        {
            Object = existing;
            return;
        }
        Object = new NetworkObject { Name = "NetworkObject", Kind = NetworkObject.ObjectKind.Custom };
        AddChild(Object);
    }

    public override void _Ready()
    {
        _time = NetfoxContext.For(this).NetworkTime;
        _time.OnTick += Move;
    }

    public override void _ExitTree() => _time.OnTick -= Move;

    private void Move(double delta, int tick)
    {
        if (!IsMultiplayerAuthority()) return;
        Location += Velocity * (float)delta;
        if (CountsTicks) Ticks++;
    }
}
