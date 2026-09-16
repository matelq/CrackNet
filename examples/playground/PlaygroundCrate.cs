using Godot;

namespace Netfox.Examples.Playground;

/// <summary>
/// A crate anyone can push, grab and throw. The peer simulating it runs Rapier on it; everyone else holds it kinematic
/// and plays back what that peer sends. Touching it takes authority over it, and it takes authority over the crates it
/// hits in turn. Once it has come to rest it goes back to the host.
/// </summary>
public partial class PlaygroundCrate : RigidBody3D
{
    private const int RestTicksBeforeReturning = 30;

    [Synced] public Transform3D NetTransform { get => GlobalTransform; set => GlobalTransform = value; }
    [Synced] public Vector3 NetLinearVelocity { get => LinearVelocity; set => LinearVelocity = value; }
    [Synced] public Vector3 NetAngularVelocity { get => AngularVelocity; set => AngularVelocity = value; }

    public NetworkObject Object { get; private set; } = null!;
    private StandardMaterial3D _material = null!;
    private int _restTicks;

    public static PlaygroundCrate Create(string name, Vector3 position)
    {
        var crate = new PlaygroundCrate { Name = name, Position = position, Mass = 2, ContactMonitor = true, MaxContactsReported = 4 };
        crate.SetMultiplayerAuthority(1);
        crate.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One } });
        crate._material = new StandardMaterial3D();
        crate.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = Vector3.One }, MaterialOverride = crate._material });
        crate.Object = new NetworkObject { Name = "NetworkObject", SpreadsAuthority = true };
        crate.AddChild(crate.Object);
        crate.AddToGroup("crates");
        return crate;
    }

    public override void _Ready()
    {
        FreezeMode = FreezeModeEnum.Kinematic;
        Object.AuthorityChanged += Refresh;
        Object.EventReceived += (_, payload) => ApplyCentralImpulse(payload.AsVector3());
        BodyEntered += OnBodyEntered;
        Playground.SlotsChanged += Refresh;
        Refresh();
    }

    public override void _ExitTree() => Playground.SlotsChanged -= Refresh;

    /// <summary>Simulated only where authoritative and not held; tinted with the simulating peer's colour.</summary>
    public void Refresh()
    {
        Freeze = !Object.IsAuthority || Object.Holder != 0;
        _material.AlbedoColor = Playground.ColorOf(Object.Authority).Lerp(Colors.SaddleBrown, 0.35f);
    }

    private void OnBodyEntered(Node other)
    {
        // Whoever simulates a moving crate simulates what it knocks over too
        if (other is PlaygroundCrate crate && Object.IsAuthority && LinearVelocity.Length() > 0.5f)
            Object.Touch(crate.Object);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!Object.IsAuthority || Object.Holder != 0 || Multiplayer.IsServer())
        {
            _restTicks = 0;
            return;
        }

        _restTicks = Sleeping || LinearVelocity.Length() < 0.05f ? _restTicks + 1 : 0;
        if (_restTicks >= RestTicksBeforeReturning && Object.ReturnToHost()) _restTicks = 0;
    }
}
