using Godot;
using FileAccess = Godot.FileAccess;

namespace CrackNet.Examples.Playground;

/// <summary>
/// A per-window log for playtests, to read afterwards rather than watch: user://playtest/&lt;start&gt;_pid&lt;pid&gt;.log,
/// one line per event, flushed at once so a killed window keeps it. Lines start with milliseconds since start, the
/// network tick, the physics frame and this window's peer (0 before connecting). The tick is the same number on every
/// peer, so two windows' logs are read against each other on it; the milliseconds are each window's own.
/// <list type="bullet">
/// <item>ACTION: what the local player did (grab, throw, shoot, push); SHOT: a shot hit a crate.</item>
/// <item>AUTH: a crate changed authority or holder here, with its state at that moment.</item>
/// <item>MOVE: ten times a second, every crate moving faster than a walk, compactly.</item>
/// <item>JUMP: a crate that moved further in one frame than its speed covers, by more than <see cref="JumpDistance"/>:
/// its body and what is drawn, each (smoothing hides a body's jump from the drawing), with how long ago it last changed
/// hands here.</item>
/// <item>LAUNCH: a crate simulated here went faster than <see cref="LaunchSpeed"/>; the half second before it follows,
/// every frame, for the crates and players within four metres.</item>
/// <item>RIDE: twice a second, every player standing on something, where it stands on it. The same player logged in
/// two windows says whether every screen has it in the same place on the platform.</item>
/// <item>BASE: a player took something to stand on, or stopped standing on it. A copy that stops riding is drawn
/// from world positions while the platform goes on without it, which looks exactly like sliding along it.</item>
/// <item>SLIDE: a player drifting along what it stands on by more than <see cref="SlideDistance"/> beyond what its
/// own walk covers - the platform carrying its copy somewhere its own peer does not have it. The half second before
/// it follows, frame by frame, for that player alone: where it was, on what, and at which display tick.</item>
/// </list>
/// Off unless <c>playground/playtest_log</c> is on in Project Settings, <c>-- --log</c> is on the command line, or F3
/// is pressed in game, which also says so on screen. Never on under <c>--smoke</c>.
/// </summary>
public static class PlaytestLog
{
    public const float LaunchSpeed = 12;
    private const float MovingSpeed = 0.3f;
    private const int FramesBefore = 30;
    public const float JumpDistance = 0.3f;

    /// <summary>Drift along what a player stands on, past its own walk, that is worth a line.</summary>
    public const float SlideDistance = 0.2f;

    private static FileAccess? _file;
    private static bool? _on;

    /// <summary>Whether the log is being written; F3 in the playground toggles it.</summary>
    public static bool On
    {
        get => _on ??= !OS.GetCmdlineUserArgs().Contains("--smoke")
                       && (OS.GetCmdlineUserArgs().Contains("--log")
                           || ProjectSettings.GetSetting("playground/playtest_log", false).AsBool());
        set => _on = value && !OS.GetCmdlineUserArgs().Contains("--smoke");
    }

    private static readonly Queue<(ulong Frame, Vector3 At, string Line)> Recent = new();
    private static readonly Dictionary<PlaygroundCrate, (Vector3 Body, Vector3 Drawn, float Speed)> Last = new();
    private static readonly Dictionary<PlaygroundCrate, ulong> ChangedAt = new();
    private static readonly Dictionary<PlaygroundPlayer, (Node3D? On, Vector3 Offset)> LastRide = new();
    private static readonly Dictionary<PlaygroundPlayer, (float Slid, ulong Since)> Sliding = new();
    private static readonly Queue<(ulong Frame, PlaygroundPlayer Player, string Line)> Ridden = new();
    private static ulong _lastLaunch;

    private static FileAccess? File(Node node)
    {
        if (!On) return null;
        if (_file is not null) return _file;
        DirAccess.MakeDirRecursiveAbsolute("user://playtest");
        var start = Time.GetDatetimeStringFromSystem().Replace(':', '-')[..16];
        _file = FileAccess.Open($"user://playtest/{start}_pid{OS.GetProcessId()}.log", FileAccess.ModeFlags.Write);
        return _file;
    }

    private static void Write(Node node, string line)
    {
        if (File(node) is not { } file) return;
        var peer = node.Multiplayer.MultiplayerPeer is null or OfflineMultiplayerPeer ? 0 : node.Multiplayer.GetUniqueId();
        file.StoreLine($"{Time.GetTicksMsec()} t{NetworkTime.Instance.Tick} f{Engine.GetPhysicsFrames()} p{peer} {line}");
        file.Flush();
    }

    private static string State(PlaygroundCrate crate)
        => $"{crate.Name} at {crate.GlobalPosition:F2} v {crate.LinearVelocity:F1} authority {crate.Authority.Peer} " +
           $"holder {crate.ClaimedBy} frozen {crate.Freeze} layer {crate.CollisionLayer}";

    public static void Action(PlaygroundPlayer player, string what) => Write(player, $"ACTION {player.Name} {what} at {player.GlobalPosition:F2}");

    /// <summary>
    /// One player's place this frame: where it is, what it stands on and where on it, how much of that is its own
    /// walk, and the tick its copy is being drawn at. The same player read from two windows lines up on that tick.
    /// </summary>
    private static string Ride(PlaygroundPlayer player, Node3D? on, Vector3 offset)
        => $"{player.Name}@{player.Peer} {(on is null ? "free" : $"on {on.Name} at {offset:F2}")} " +
           $"world {player.GlobalPosition:F2} drawn {player.GetNode<Node3D>("Visual").GlobalPosition:F2} " +
           $"walk {player.WalkBlend:F2} shown {player.Net().Diagnostics.DisplayTick?.ToString("F1") ?? "own"}" +
           // The base is drawn at a moment of its own: a rider placed on it at another one is placed wrong
           (on is null ? "" : $" base shown {on.Net().Diagnostics.DisplayTick?.ToString("F1") ?? "own"}");

    public static void Note(Node node, string what) => Write(node, what);

    public static void Authority(PlaygroundCrate crate)
    {
        ChangedAt[crate] = Time.GetTicksMsec();
        Write(crate, $"AUTH {State(crate)}");
    }

    /// <summary>Once per physics frame, from the playground.</summary>
    public static void Frame(Node playground, IReadOnlyList<PlaygroundCrate> crates, IReadOnlyList<PlaygroundPlayer> players)
    {
        var frame = Engine.GetPhysicsFrames();
        foreach (var crate in crates) Recent.Enqueue((frame, crate.GlobalPosition, State(crate)));
        foreach (var player in players) Recent.Enqueue((frame, player.GlobalPosition, $"{player.Name} at {player.GlobalPosition:F2} v {player.Velocity:F1}"));
        while (Recent.Count > 0 && Recent.Peek().Frame + FramesBefore < frame) Recent.Dequeue();

        var delta = (float)playground.GetPhysicsProcessDeltaTime();
        foreach (var crate in crates)
        {
            var speed = crate.LinearVelocity.Length();
            var drawn = crate.GetNode<Node3D>("Visual").GlobalPosition;
            if (Last.TryGetValue(crate, out var last))
            {
                var covered = Mathf.Max(speed, last.Speed) * delta;
                var body = crate.GlobalPosition.DistanceTo(last.Body) - covered;
                var shown = drawn.DistanceTo(last.Drawn) - covered;
                if (body > JumpDistance || shown > JumpDistance)
                {
                    var since = ChangedAt.TryGetValue(crate, out var at) ? $"{Time.GetTicksMsec() - at} ms" : "never";
                    Write(playground, $"JUMP {crate.Name} body {body:F2} m, drawn {shown:F2} m beyond its speed, body from " +
                                      $"{last.Body:F2} to {crate.GlobalPosition:F2}, authority {crate.Authority.Peer}, changed hands {since} ago");
                }
            }
            Last[crate] = (crate.GlobalPosition, drawn, speed);
        }

        // A player carried by a platform its own peer has elsewhere drifts along it here. Judged on the offset from
        // what it stands on, so the platform's own travel is out of it, against what the player's walk covers: the
        // walk blend is synced, so a copy knows how much of the motion is the player's own even though it is played
        // back rather than simulated
        foreach (var player in players)
        {
            var on = player.Net().Diagnostics.StandingOn as Node3D;
            var offset = on is null ? player.GlobalPosition : on.GlobalTransform.AffineInverse() * player.GlobalPosition;
            Ridden.Enqueue((frame, player, Ride(player, on, offset)));
            // A rider is put on its base every frame, so a copy still riding cannot drift along it: a drift means it
            // stopped riding and is being drawn from world positions while the platform goes on without it
            if (LastRide.TryGetValue(player, out var had) && !ReferenceEquals(had.On, on))
                Write(playground, $"BASE {player.Name} {(on is null ? $"let go of {had.On!.Name}, last on it at {had.Offset:F2}" : $"took {on.Name}")}, " +
                                  Ride(player, on, offset));
            if (LastRide.TryGetValue(player, out var was) && ReferenceEquals(was.On, on) && on is not null)
            {
                var slid = offset.DistanceTo(was.Offset) - (player.WalkBlend * PlaygroundPlayer.Speed * delta + 0.01f);
                var (total, since) = Sliding.TryGetValue(player, out var run) ? run : (0f, Time.GetTicksMsec());
                // A leaky bucket. A drift spread over several frames still adds up to something worth a line, but a
                // frame inside the allowance drains what was collected rather than leaving it standing: summing every
                // frame's float noise reaches any threshold given a minute, and then reports the minute's worth as if
                // it had happened in the last two frames
                total = Math.Max(0, total + slid);
                if (total <= 0) since = Time.GetTicksMsec();
                if (total > SlideDistance)
                {
                    Write(playground, $"SLIDE {player.Name} slid {total:F2} m along {on.Name} in {Time.GetTicksMsec() - since} ms " +
                                      $"beyond its own walk (blend {player.WalkBlend:F2})");
                    foreach (var (entryFrame, _, line) in Ridden.Where(entry => ReferenceEquals(entry.Player, player)))
                        Write(playground, $"  f{entryFrame} {line}");
                    total = 0;
                    since = Time.GetTicksMsec();
                }
                Sliding[player] = (total, since);
            }
            LastRide[player] = (on, offset);
        }
        while (Ridden.Count > 0 && Ridden.Peek().Frame + FramesBefore < frame) Ridden.Dequeue();

        // Often enough to follow a run along a platform, and cheap: one line for everyone standing on something
        if (frame % 6 == 0)
        {
            var riding = players.Where(player => player.Net().Diagnostics.StandingOn is Node3D)
                .Select(player => Ride(player, (Node3D)player.Net().Diagnostics.StandingOn!,
                    ((Node3D)player.Net().Diagnostics.StandingOn!).GlobalTransform.AffineInverse() * player.GlobalPosition));
            var ride = string.Join("; ", riding);
            if (ride.Length > 0) Write(playground, $"RIDE {ride}");
        }

        if (frame % 6 == 0)
        {
            var moving = crates.Where(crate => crate.LinearVelocity.Length() > MovingSpeed && crate.PlaybackState == PlaybackState.Playing)
                .Select(crate => $"{crate.Name}@{crate.Authority.Peer} {crate.GlobalPosition:F1} {crate.LinearVelocity.Length():F1}");
            var line = string.Join("; ", moving);
            if (line.Length > 0) Write(playground, $"MOVE {line}");
        }

        if (frame < _lastLaunch + 120) return;
        if (crates.FirstOrDefault(crate => !crate.Freeze && crate.LinearVelocity.Length() > LaunchSpeed) is not { } launched) return;
        _lastLaunch = frame;
        Write(playground, $"LAUNCH {State(launched)}");
        foreach (var (at, entryFrame, line) in Recent.Select(entry => (entry.At, entry.Frame, entry.Line)))
            if (at.DistanceTo(launched.GlobalPosition) < 4) Write(playground, $"  f{entryFrame} {line}");
    }
}
