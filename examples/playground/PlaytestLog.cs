using Godot;
using FileAccess = Godot.FileAccess;

namespace Netfox.Examples.Playground;

/// <summary>
/// A per-window log for playtests, to read afterwards rather than watch: user://playtest/&lt;start&gt;_pid&lt;pid&gt;.log,
/// one line per event, flushed at once so a killed window keeps it. Lines start with milliseconds since start,
/// physics frame and this window's peer (0 before connecting).
/// <list type="bullet">
/// <item>ACTION: what the local player did (grab, throw, shoot, push); SHOT: a shot hit a crate.</item>
/// <item>AUTH: a crate changed authority or holder here, with its state at that moment.</item>
/// <item>MOVE: ten times a second, every crate moving faster than a walk, compactly.</item>
/// <item>LAUNCH: a crate simulated here went faster than <see cref="LaunchSpeed"/>; the half second before it follows,
/// every frame, for the crates and players within four metres.</item>
/// </list>
/// Off unless <c>playground/playtest_log</c> is on in Project Settings, <c>-- --log</c> is on the command line, or F3
/// is pressed in game, which also says so on screen. Never on under <c>--smoke</c>.
/// </summary>
public static class PlaytestLog
{
    public const float LaunchSpeed = 12;
    private const float MovingSpeed = 0.3f;
    private const int FramesBefore = 30;

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
        file.StoreLine($"{Time.GetTicksMsec()} f{Engine.GetPhysicsFrames()} p{peer} {line}");
        file.Flush();
    }

    private static string State(PlaygroundCrate crate)
        => $"{crate.Name} at {crate.GlobalPosition:F2} v {crate.LinearVelocity:F1} authority {crate.Object.Authority.Peer} " +
           $"holder {crate.Object.Holder} frozen {crate.Freeze} layer {crate.CollisionLayer}";

    public static void Action(PlaygroundPlayer player, string what) => Write(player, $"ACTION {player.Name} {what} at {player.GlobalPosition:F2}");

    public static void Note(Node node, string what) => Write(node, what);

    public static void Authority(PlaygroundCrate crate) => Write(crate, $"AUTH {State(crate)}");

    /// <summary>Once per physics frame, from the playground.</summary>
    public static void Frame(Node playground, IReadOnlyList<PlaygroundCrate> crates, IEnumerable<PlaygroundPlayer> players)
    {
        var frame = Engine.GetPhysicsFrames();
        foreach (var crate in crates) Recent.Enqueue((frame, crate.GlobalPosition, State(crate)));
        foreach (var player in players) Recent.Enqueue((frame, player.GlobalPosition, $"{player.Name} at {player.GlobalPosition:F2} v {player.Velocity:F1}"));
        while (Recent.Count > 0 && Recent.Peek().Frame + FramesBefore < frame) Recent.Dequeue();

        if (frame % 6 == 0)
        {
            var moving = crates.Where(crate => crate.LinearVelocity.Length() > MovingSpeed && crate.Visible)
                .Select(crate => $"{crate.Name}@{crate.Object.Authority.Peer} {crate.GlobalPosition:F1} {crate.LinearVelocity.Length():F1}");
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
