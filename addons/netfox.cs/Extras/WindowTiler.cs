using Godot;
using Netfox.Core.Logging;
using Netfox.Core.Time;
using Netfox.Internal;
using FileAccess = Godot.FileAccess;

namespace Netfox.Extras;

/// <summary>Tiles the windows of game instances launched together from the editor. Port of netfox.extras/window-tiler.gd.</summary>
public partial class WindowTiler : Node
{
    private static readonly NetfoxLogger Logger = NetfoxLogger.ForExtras("WindowTiler");

    private readonly bool _isEnabled = NetfoxSettings.Instance.AutoTileWindows;
    private readonly bool _isBorderless = NetfoxSettings.Instance.Borderless;
    private readonly int _tileScreen = NetfoxSettings.Instance.TileScreen;

    // Hash the game name so the lock file names are always valid
    private readonly string _prefix = $"netfox-window-tiler-{Settings.GetString("application/config/name", "godot").GetHashCode():x}";
    private readonly string _sid = $"{((long)(Clocks.UnixTime() / 2.0)).GetHashCode():x}";
    private readonly string _uid = $"{(long)(Clocks.UnixTime() * 1000_0000.0)}";

    public override async void _Ready()
    {
        if (OS.HasFeature("template")) return;
        if (DisplayServer.GetName() == "headless") return;

        foreach (var envVar in new[] { "CI", "NETFOX_CI" })
        {
            if (OS.GetEnvironment(envVar).Length == 0) continue;
            Logger.Debug("Environment variable {0} set, disabling", envVar);
            return;
        }

        if (!HasRecentLocks(3)) Cleanup();

        if (IsEmbedded()) return;
        if (!_isEnabled) return;

        Logger.Debug("Tiling with sid: {0}, uid: {1}", _sid, _uid);

        var error = MakeLock(_sid, _uid);
        if (error != Error.Ok)
        {
            Logger.Warning("Failed to create lock for tiling, reason: {0}", error);
            return;
        }

        // Poll locks until no new ones show up
        var locks = new List<string>();
        var stablePolls = 0;
        await ToSignal(GetTree().CreateTimer(0.25), SceneTreeTimer.SignalName.Timeout);

        for (var i = 0; i < 20; i++)
        {
            await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
            if (!IsInsideTree()) return;

            var newLocks = ListLockIds();
            if (newLocks.SequenceEqual(locks))
            {
                stablePolls++;
            }
            else
            {
                locks = newLocks;
                stablePolls = 0;
            }

            if (stablePolls >= 2) break;
        }

        var idx = locks.IndexOf(_uid);
        Logger.Debug("Tiling as idx {0} / {1} - {2} in {3}", idx, locks.Count, _uid, string.Join(", ", locks));
        TileWindow(idx, locks.Count);
    }

    private static bool IsEmbedded()
        => Engine.Singleton.HasMethod("is_embedded_in_editor") && Engine.Singleton.Call("is_embedded_in_editor").AsBool();

    private Error MakeLock(string sid, string uid)
    {
        var path = $"{OS.GetCacheDir()}/{_prefix}-{sid}-{uid}";
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        return file is null ? FileAccess.GetOpenError() : Error.Ok;
    }

    private List<string> ListLockIds()
    {
        var result = new List<string>();
        using var dir = DirAccess.Open(OS.GetCacheDir());
        if (dir is null) return result;

        foreach (var f in dir.GetFiles())
            if (f.StartsWith(_prefix)) result.Add(GetUid(f));
        result.Sort(string.CompareOrdinal);
        return result;
    }

    private void Cleanup()
    {
        using var dir = DirAccess.Open(OS.GetCacheDir());
        if (dir is null) return;

        foreach (var f in dir.GetFiles())
        {
            if (!f.StartsWith(_prefix)) continue;
            Logger.Trace("Cleaned lock: {0}", f);
            dir.Remove($"{OS.GetCacheDir()}/{f}");
        }
    }

    private bool HasRecentLocks(int seconds)
    {
        using var dir = DirAccess.Open(OS.GetCacheDir());
        if (dir is null) return false;

        var now = (long)Clocks.UnixTime();
        foreach (var f in dir.GetFiles())
        {
            if (!f.StartsWith(_prefix)) continue;
            var modified = (long)FileAccess.GetModifiedTime($"{OS.GetCacheDir()}/{f}");
            if (modified > 0 && now - modified <= seconds) return true;
        }
        return false;
    }

    private string GetUid(string filename)
    {
        var rest = filename.Substring(_prefix.Length + 1);
        var parts = rest.Split('-');
        return parts.Length > 1 ? parts[1] : rest;
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
