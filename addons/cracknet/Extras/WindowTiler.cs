using CrackNet.Core.Logging;
using CrackNet.Core.Time;
using CrackNet.Internal;
using Godot;
using FileAccess = Godot.FileAccess;

namespace CrackNet.Extras;

/// <summary>
/// Tiles the windows of game instances launched together from the editor (Debug > Customize Run Instances), on
/// Project Settings > CrackNet > Extras > Auto Tile Windows.
/// <para>
/// Every instance keeps a lock file in the cache directory fresh twice a second; a lock that has not been touched for
/// a few seconds belongs to an instance that is gone. Each instance lays itself out again whenever the set of live
/// locks changes. The original decided once, in its first two seconds, and deleted every lock older than three:
/// instances that take several seconds each to start - C# and a physics extension - each saw only themselves and
/// all maximised on top of each other.
/// </para>
/// </summary>
public partial class WindowTiler : Node
{
    private static readonly CrackNetLogger Logger = CrackNetLogger.ForExtras("WindowTiler");

    private const double TouchInterval = 0.5;
    private const long StaleSeconds = 3;

    private readonly bool _isEnabled = CrackNetSettings.Instance.AutoTileWindows;
    private readonly bool _isBorderless = CrackNetSettings.Instance.Borderless;
    private readonly int _tileScreen = CrackNetSettings.Instance.TileScreen;

    // Hash the game name so the lock file names are always valid. Not string.GetHashCode: .NET randomises it per
    // process, so every instance had its own prefix and saw only its own lock (GDScript's hash() is stable)
    private readonly string _prefix = $"cracknet-window-tiler-{StableHash(Settings.GetString("application/config/name", "godot")):x8}-";

    private static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text) hash = (hash ^ c) * 16777619u;
        return hash;
    }
    private readonly string _uid = $"{(long)(Clocks.UnixTime() * 1000_0000.0)}";

    private string LockPath => $"{OS.GetCacheDir()}/{_prefix}{_uid}";

    private List<string> _layout = new();
    private double _sinceTouch = TouchInterval;
    private bool _active;

    public override void _Ready()
    {
        if (OS.HasFeature("template") || DisplayServer.GetName() == "headless" || !_isEnabled || IsEmbedded()) return;

        foreach (var envVar in new[] { "CI", "CRACKNET_CI" })
        {
            if (OS.GetEnvironment(envVar).Length == 0) continue;
            Logger.Debug("Environment variable {0} set, disabling", envVar);
            return;
        }

        _active = true;
    }

    public override void _Process(double delta)
    {
        if (!_active) return;
        _sinceTouch += delta;
        if (_sinceTouch < TouchInterval) return;
        _sinceTouch = 0;

        using (var file = FileAccess.Open(LockPath, FileAccess.ModeFlags.Write))
        {
            if (file is null)
            {
                Logger.Warning("Failed to write the tiling lock, reason: {0}", FileAccess.GetOpenError());
                _active = false;
                return;
            }
            file.StoreString(_uid);
        }

        var live = LiveLocks();
        if (live.SequenceEqual(_layout)) return;

        _layout = live;
        var index = live.IndexOf(_uid);
        Logger.Debug("Tiling as {0} of {1}", index, live.Count);
        if (index >= 0) TileWindow(index, live.Count);
    }

    public override void _ExitTree()
    {
        if (_active && FileAccess.FileExists(LockPath)) DirAccess.RemoveAbsolute(LockPath);
    }

    private static bool IsEmbedded()
        => Engine.Singleton.HasMethod("is_embedded_in_editor") && Engine.Singleton.Call("is_embedded_in_editor").AsBool();

    /// <summary>The uids of instances that touched their lock recently, oldest first; stale locks are removed.</summary>
    private List<string> LiveLocks()
    {
        var result = new List<string>();
        using var dir = DirAccess.Open(OS.GetCacheDir());
        if (dir is null) return result;

        var now = (long)Clocks.UnixTime();
        foreach (var name in dir.GetFiles())
        {
            if (!name.StartsWith(_prefix)) continue;
            var path = $"{OS.GetCacheDir()}/{name}";
            if (now - (long)FileAccess.GetModifiedTime(path) > StaleSeconds)
            {
                dir.Remove(path);
                continue;
            }
            // A lock from the older format, or anything else sharing the prefix, is not ours to order
            if (long.TryParse(name[_prefix.Length..], out _)) result.Add(name[_prefix.Length..]);
        }

        // Uids are start times, so sorting keeps each window's place as others come and go
        result.Sort((a, b) => long.Parse(a).CompareTo(long.Parse(b)));
        return result;
    }

    private void TileWindow(int i, int total)
    {
        var screenRect = DisplayServer.ScreenGetUsableRect(_tileScreen);
        var window = GetTree().Root;
        window.CurrentScreen = _tileScreen;

        if (total == 1)
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Maximized);
            return;
        }

        DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
        window.Borderless = _isBorderless;

        var windowsPerRow = (int)Math.Ceiling(Math.Sqrt(total));
        var windowsPerCol = (int)Math.Ceiling(total / (double)windowsPerRow);
        var windowSize = new Vector2I(screenRect.Size.X / windowsPerRow, screenRect.Size.Y / windowsPerCol);

        window.Size = windowSize;

        var row = i / windowsPerRow;
        var col = i % windowsPerRow;
        window.Position = new Vector2I(screenRect.Position.X + col * windowSize.X, screenRect.Position.Y + row * windowSize.Y);
    }
}
