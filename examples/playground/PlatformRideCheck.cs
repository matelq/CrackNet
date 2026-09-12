using Godot;

namespace Netfox.Examples.Playground;

/// <summary>
/// Puts a player on the moving platform and checks that it is carried rather than slid off, in one process with an
/// offline peer - so nothing here depends on packet timing.
/// <para>
/// This exists because the failure is quiet and specific to rollback. Godot carries a rider by applying the
/// platform's per-<i>frame</i> velocity inside every <c>MoveAndSlide</c>; netfox calls MoveAndSlide twice per tick
/// and runs several ticks per frame while resimulating, so that motion is applied several times over and the rider
/// walks off the end. <see cref="PlayerCharacter.RideFloor"/> replaces it with a per-tick carry, and this is what
/// says so.
/// </para>
/// <para>
/// Run: <c>godot --headless --path . res://examples/playground/PlatformRideCheck.tscn</c>
/// </para>
/// </summary>
public partial class PlatformRideCheck : Node3D
{
    private const int Settle = 30;
    private const int Ticks = 150;
    private const float Tolerance = 0.05f;

    private MovingPlatform _platform = null!;
    private PlayerCharacter _player = null!;
    private Vector3 _offset;
    private float _drift;
    private bool _done;

    public override async void _Ready()
    {
        var ground = new StaticBody3D { Name = "Ground", Position = new Vector3(0, -4, 0) };
        ground.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(60, 1, 60) } });
        AddChild(ground);

        _platform = new MovingPlatform { Name = "Platform", Position = new Vector3(0, 1, 0) };
        _platform.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(6, 0.5f, 4) } });
        _platform.AddChild(new RollbackSynchronizer
        {
            Name = "RollbackSynchronizer",
            Root = _platform,
            StateProperties = [":position"],
        });
        AddChild(_platform);

        _player = GD.Load<PackedScene>("res://examples/playground/player.tscn").Instantiate<PlayerCharacter>();
        _player.Name = "Player_1";
        _player.Position = new Vector3(0, 2.2f, 0);
        AddChild(_player);

        Multiplayer.MultiplayerPeer = new OfflineMultiplayerPeer();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        NetworkTime.Instance.AfterTickLoop += Measure;
        NetworkTime.Instance.Start();
    }

    /// <summary>
    /// The offset is taken once the player has landed, and every tick after that has to reproduce it. Only the
    /// horizontal part: the platform travels in X, and the vertical settling is not what is under test.
    /// </summary>
    private void Measure()
    {
        if (_done) return;
        var tick = NetworkTime.Instance.Tick;

        if (tick == Settle) _offset = Flat(_player.Position - _platform.Position);
        if (tick > Settle) _drift = Mathf.Max(_drift, (Flat(_player.Position - _platform.Position) - _offset).Length());
        if (tick < Ticks) return;

        _done = true;
        Report();
    }

    private static Vector3 Flat(Vector3 value) => value with { Y = 0 };

    private void Report()
    {
        // A resimulation is where this used to come apart: the same ticks run again inside one frame, and anything
        // carrying the rider by a frame's worth of motion moves it several times as far the second time round
        var driftBefore = _drift;
        NetworkRollback.Instance.NotifyResimulationStart(NetworkTime.Instance.Tick - 20);
        NetworkTime.Instance.RunAfterTickLoop();
        var resimDrift = (Flat(_player.Position - _platform.Position) - _offset).Length();

        var moved = Mathf.Abs(_platform.Position.X) > 1.0f;
        var rode = _drift < Tolerance;
        var survivedResim = resimDrift < Tolerance;
        var onPlatform = _player.Position.Y > 0.5f;
        var ok = moved && rode && survivedResim && onPlatform;

        var head = FormattableString.Invariant(
            $"PLATFORM RIDE ok={ok} ticks={NetworkTime.Instance.Tick} platform_x={_platform.Position.X:F2} moved={moved}");
        var tail = FormattableString.Invariant(
            $"drift={driftBefore:F4} resim_drift={resimDrift:F4} tolerance={Tolerance} player_y={_player.Position.Y:F2} offset_x={_offset.X:F3}");
        GD.Print($"{head} {tail}");

        GetTree().Quit(ok ? 0 : 1);
    }
}
