using Godot;

namespace Netfox.Examples.Playground;

/// <summary>
/// Drives the playground scene headless so it can be checked without a window: host or join, press the button, wait,
/// and report what happened. The sample itself is meant to be played; this is what keeps it from rotting.
/// <para>
/// Run: <c>godot --headless --path . res://examples/playground/PlaygroundSmoke.tscn -- --host --seconds=12</c>
/// and, a moment later, the same with <c>--join --seconds=8</c>.
/// </para>
/// </summary>
public partial class PlaygroundSmoke : Node
{
    private bool _isHost;
    private double _seconds = 10;
    private Playground _playground = null!;

    // The two processes cannot both report while the other is alive - whoever outlives the other sees it leave. So
    // the count is the peak, not the count at the end.
    private int _maxPlayers;

    public override void _Process(double delta)
    {
        var players = _playground?.GetNodeOrNull("World/Players");
        if (players is not null) _maxPlayers = Mathf.Max(_maxPlayers, players.GetChildCount());
    }

    public override async void _Ready()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg == "--host") _isHost = true;
            else if (arg == "--join") _isHost = false;
            else if (arg.StartsWith("--seconds=")) _seconds = double.Parse(arg["--seconds=".Length..], System.Globalization.CultureInfo.InvariantCulture);
        }

        _playground = GD.Load<PackedScene>("res://examples/playground/playground.tscn").Instantiate<Playground>();
        AddChild(_playground);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        // The same buttons a player would press
        _playground.GetNode<Button>(_isHost ? "UI/Lobby/Panel/Rows/Buttons/Host" : "UI/Lobby/Panel/Rows/Buttons/Join")
            .EmitSignal(BaseButton.SignalName.Pressed);

        // Hold a direction briefly, so the players move and the synchronizers have something to replicate, then let
        // go well before anyone walks off the ground
        Godot.Input.ParseInputEvent(new InputEventAction { Action = "ui_right", Pressed = true });
        await ToSignal(GetTree().CreateTimer(2.0), Godot.Timer.SignalName.Timeout);
        Godot.Input.ParseInputEvent(new InputEventAction { Action = "ui_right", Pressed = false });

        // One shot, so the weapon's request and accept round trip is exercised too
        Godot.Input.ParseInputEvent(new InputEventAction { Action = "ui_select", Pressed = true });
        await ToSignal(GetTree().CreateTimer(0.3), Godot.Timer.SignalName.Timeout);
        Godot.Input.ParseInputEvent(new InputEventAction { Action = "ui_select", Pressed = false });

        await ToSignal(GetTree().CreateTimer(_seconds), Godot.Timer.SignalName.Timeout);
        Report();
    }

    private void Report()
    {
        var players = _playground.GetNode("World/Players").GetChildren().OfType<PlayerCharacter>().ToList();
        var platform = _playground.GetNode<MovingPlatform>("World/Platform");
        var self = players.FirstOrDefault(player => player.IsLocal);

        var crates = _playground.GetNode("World/Crates").GetChildren().OfType<Node3D>().ToList();
        var scoreboard = _playground.GetNode<Scoreboard>("World/Scoreboard");
        var physics = _playground.GetNode<PhysicsTier>("PhysicsTier");

        var ok = NetworkTime.Instance.IsInitialSyncDone()
                 // Run as a pair, so both sides have to have seen both players at some point
                 && _maxPlayers >= 2
                 && self is not null
                 // Moved under its own input, and is standing on the ground rather than having fallen through it
                 && self.Position.X > 2
                 && self.Position.Y is > 0.5f and < 3
                 && self.JumpsLeft == self.MaxJumps
                 // Started Airborne and fell: reaching Grounded means the rewindable state machine transitioned
                 && self.StateMachine.State == "Grounded"
                 // Each peer fires its own player, and the shot has to have been accepted. Whether the other peer's
                 // shot is seen depends on who was connected when - the host fires before the client has joined.
                 && self.Weapon.Shots > 0
                 // The scoreboard is replicated without rollback, so both peers see the host's count
                 && scoreboard.Shots > 0
                 // The platform is simulated from the tick, so it is somewhere other than where it started
                 && Mathf.Abs(platform.Position.X) > 0.1f;

        var names = string.Join(",", players.Select(player => player.Name));
        var head = $"PLAYGROUND role={(_isHost ? "host" : "client")} ok={ok} peer=#{Multiplayer.GetUniqueId()} " +
                   $"tick={NetworkTime.Instance.Tick} synced={NetworkTime.Instance.IsInitialSyncDone()}";
        var tail = FormattableString.Invariant(
            $"players=[{names}] peak={_maxPlayers} physics={(physics.Active ? "rapier" : physics.Reason)} crates={crates.Count} state={self?.StateMachine.State} shots={string.Join("/", players.Select(p => p.Weapon.Shots))} score={scoreboard.Shots} platform_x={platform.Position.X:F2} own_pos={self?.Position} jumps={self?.JumpsLeft}");
        GD.Print($"{head} {tail}");

        GetTree().Quit(ok ? 0 : 1);
    }
}
