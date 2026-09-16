using Godot;

namespace Netfox.Tests;

/// <summary>A replicated body for harness cases: while authoritative it moves along <see cref="Velocity"/> every tick.</summary>
public partial class HarnessBody : Node3D
{
    [Synced] public Vector3 Location { get; set; }
    [Synced] public int Ticks { get; set; }

    public Vector3 Velocity { get; set; }
    public NetworkObject Object { get; private set; } = null!;

    private NetworkTime _time = null!;

    /// <summary>Adds a body named <paramref name="name"/> under <paramref name="stack"/>, owned by <paramref name="authority"/>.</summary>
    public static HarnessBody Spawn(Node stack, string name, int authority, Vector3 velocity = default, Vector3 location = default)
    {
        var body = new HarnessBody { Name = name, Velocity = velocity, Location = location };
        body.SetMultiplayerAuthority(authority);
        body.Object = new NetworkObject { Name = "NetworkObject" };
        body.AddChild(body.Object);
        stack.AddChild(body);
        return body;
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
        Ticks++;
    }
}
