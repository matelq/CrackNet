using Godot;

namespace Netfox.Examples.Playground;

/// <summary>
/// Can one player shove another, and does standing on the moving platform change the answer?
/// <para>
/// A <c>CharacterBody3D</c> is not supposed to be pushable at all: neither <c>MoveAndSlide</c> nor
/// <c>MoveAndCollide</c> moves any body but the one it is called on. So if a player can be shoved, something is
/// moving it without collision - and the report behind netfox-net#41 was that this happens on the platform and
/// nowhere else, which is exactly the shape of a rider being carried by an assignment to <c>Position</c> rather than
/// by a swept move.
/// </para>
/// <para>
/// Two pairs, so the answer is a comparison rather than a number: one pair on the ground, one riding the platform. In
/// each pair the left player walks into the right one, which stands still, and the check asks whether the right one
/// moved. On the ground it must not. On the platform it must not either, beyond travelling with the platform.
/// </para>
/// <para>
/// One process and an offline peer: nothing here is about the network. If two players can shove each other locally,
/// two peers will disagree about by how much, and no amount of replication fixes that.
/// </para>
/// <para>
/// Run: <c>godot --headless --path . res://examples/playground/PlatformPushCheck.tscn</c>
/// </para>
/// </summary>
public partial class PlatformPushCheck : Node3D
{
    private const int Settle = 40;
    private const int Ticks = 90;

    /// <summary>How far a player that nobody should be able to move may move. Two millimetres of settling.</summary>
    private const float Tolerance = 0.02f;

    private MovingPlatform _platform = null!;
    private PlayerCharacter _groundPusher = null!;
    private PlayerCharacter _groundVictim = null!;
    private PlayerCharacter _ridingPusher = null!;
    private PlayerCharacter _ridingVictim = null!;

    private Vector3 _groundVictimAt;
    private Vector3 _ridingVictimOffset;
    private float _groundDrift;
    private float _ridingDrift;

    // Contact is transient: MoveAndSlide walks the pusher around its victim within a second, and on the platform it
    // then walks off the edge. So both are tracked as "did it ever happen", not "is it happening at the end".
    private float _groundClosest = float.PositiveInfinity;
    private float _ridingClosest = float.PositiveInfinity;
    private bool _ridingStayedUp = true;
    private bool _done;

    /// <summary>
    /// The control: nobody presses anything, so the two riders only stand next to each other. If the victim still
    /// drifts, being carried is what displaces it and the pusher is innocent; if it does not, the pushing is real.
    /// </summary>
    private bool _noPush;

    /// <summary>The rider's offset from the platform every tick, to say when it parted company rather than only that it did.</summary>
    private readonly List<string> _trail = new();

    public override async void _Ready()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg == "--no-push") _noPush = true;

        var ground = new StaticBody3D { Name = "Ground", Position = new Vector3(0, -0.5f, 0) };
        ground.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(80, 1, 80) } });
        AddChild(ground);

        _platform = new MovingPlatform { Name = "Platform", Position = new Vector3(0, 1, -8) };
        _platform.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(4, 0.4f, 4) } });
        _platform.AddChild(new RollbackSynchronizer
        {
            Name = "RollbackSynchronizer",
            Root = _platform,
            StateProperties = [":position"],
        });
        AddChild(_platform);

        // On the ground: resting origin is half a capsule above the surface. Just over touching, so they settle
        // rather than starting inside each other, which is a different question.
        _groundPusher = Spawn("Ground_Pusher", new Vector3(-1.0f, 1.0f, 8), pushes: true);
        _groundVictim = Spawn("Ground_Victim", new Vector3(-0.1f, 1.0f, 8), pushes: false);

        // On the platform: its top face is at 1.2, so a rider rests at 2.1
        _ridingPusher = Spawn("Riding_Pusher", new Vector3(-1.0f, 2.2f, -8), pushes: true);
        _ridingVictim = Spawn("Riding_Victim", new Vector3(-0.1f, 2.2f, -8), pushes: false);

        Multiplayer.MultiplayerPeer = new OfflineMultiplayerPeer();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        NetworkTime.Instance.AfterTickLoop += Measure;
        NetworkTime.Instance.Start();
    }

    /// <summary>
    /// A pusher gathers input from the keyboard like any player; a victim's input node is owned by a peer that does
    /// not exist here, so <c>BaseNetInput</c> never gathers for it and its movement stays zero. netfox still
    /// simulates it, by prediction, which is what keeps it standing rather than frozen.
    /// </summary>
    private PlayerCharacter Spawn(string name, Vector3 at, bool pushes)
    {
        var player = GD.Load<PackedScene>("res://examples/playground/player.tscn").Instantiate<PlayerCharacter>();
        player.Name = name;
        player.Position = at;
        AddChild(player);

        if (!pushes) player.GetNode<PlayerInput>("Input").SetMultiplayerAuthority(2);
        return player;
    }

    private void Measure()
    {
        if (_done) return;
        var tick = NetworkTime.Instance.Tick;

        if (tick == Settle)
        {
            // Everyone has landed. Record where the two victims are, then lean on the arrow key.
            _groundVictimAt = _groundVictim.Position;
            _ridingVictimOffset = _ridingVictim.Position - _platform.Position;
            if (!_noPush) Godot.Input.ParseInputEvent(new InputEventAction { Action = "ui_right", Pressed = true });
        }

        if (tick > Settle)
        {
            _groundDrift = Mathf.Max(_groundDrift, (_groundVictim.Position - _groundVictimAt).Length());
            _ridingDrift = Mathf.Max(_ridingDrift,
                (_ridingVictim.Position - _platform.Position - _ridingVictimOffset).Length());

            var offset = _ridingVictim.Position - _platform.Position - _ridingVictimOffset;
            if (tick % 5 == 0)
            {
                var pusherOffset = _ridingPusher.Position - _platform.Position;
                var a = FormattableString.Invariant($"@{tick} dx={offset.X:F3}");
                var b = FormattableString.Invariant(
                    $"pusher_dx={pusherOffset.X:F2} y={_ridingVictim.Position.Y:F2} vx={_ridingVictim.Velocity.X:F2}");
                _trail.Add($"{a} floor={(_ridingVictim.IsOnFloor() ? 1 : 0)} state={_ridingVictim.StateMachine.State} {b}");
            }

            _groundClosest = Mathf.Min(_groundClosest, _groundPusher.Position.DistanceTo(_groundVictim.Position));
            _ridingClosest = Mathf.Min(_ridingClosest, _ridingPusher.Position.DistanceTo(_ridingVictim.Position));
            if (_ridingVictim.Position.Y < 1.5f) _ridingStayedUp = false;
        }

        if (tick < Ticks) return;
        _done = true;
        Godot.Input.ParseInputEvent(new InputEventAction { Action = "ui_right", Pressed = false });
        Report();
    }

    private void Report()
    {
        // The pushers have to have actually reached their victims at some point, or nothing was tested. Two
        // capsules of radius 0.4 in contact are 0.8 apart.
        var groundMet = _groundClosest < 0.9f;
        var ridingMet = _ridingClosest < 0.9f;

        // And the ridden victim has to have stayed on the platform throughout, or it was measured on the ground
        var stillRiding = _ridingStayedUp;

        var groundOk = _groundDrift < Tolerance;
        var ridingOk = _ridingDrift < Tolerance;

        // The control presses nothing, so there is no contact to insist on - only the drift has to be zero
        var ok = groundOk && ridingOk && (_noPush || (groundMet && ridingMet && stillRiding));

        var head = FormattableString.Invariant(
            $"PLATFORM PUSH ok={ok} mode={(_noPush ? "control" : "push")} ground_drift={_groundDrift:F4} riding_drift={_ridingDrift:F4} tolerance={Tolerance}");
        var met = FormattableString.Invariant(
            $"ground_met={groundMet} riding_met={ridingMet} still_riding={stillRiding}");
        var closest = FormattableString.Invariant(
            $"ground_closest={_groundClosest:F3} riding_closest={_ridingClosest:F3} platform_x={_platform.Position.X:F2}");
        var tail = $"{met} {closest} riding_victim={_ridingVictim.Position}";
        GD.Print($"{head} {tail}");
        GD.Print($"PLATFORM PUSH trail{(_noPush ? " (no-push control)" : "")}: {string.Join("  ", _trail)}");

        GetTree().Quit(ok ? 0 : 1);
    }
}
