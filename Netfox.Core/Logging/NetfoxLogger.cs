namespace Netfox.Core.Logging;

public enum LogLevel
{
    All,
    Trace,
    Debug,
    Info,
    Warn,
    Error,
    None,
}

/// <summary>
/// Logger with per-module levels and context tags. Port of netfox.internals/logger.gd.
/// Output sinks are pluggable so the core stays engine-agnostic; Netfox.Godot wires them to GD.Print / PushWarning / PushError.
/// </summary>
public sealed class NetfoxLogger
{
    public const LogLevel DefaultLogLevel = LogLevel.Debug;

    private static readonly string[] LevelPrefixes = ["", "TRC", "DBG", "INF", "WRN", "ERR", ""];

    public static LogLevel Level { get; set; } = DefaultLogLevel;
    public static Dictionary<string, LogLevel> ModuleLevels { get; } = new();
    public static bool PushToDebugger { get; set; } = true;

    public static Action<string> Print { get; set; } = Console.WriteLine;
    public static Action<string> PushWarning { get; set; } = _ => { };
    public static Action<string> PushError { get; set; } = _ => { };

    private static readonly SortedDictionary<int, List<Func<string>>> Tags = new();
    private static readonly List<Func<string>> OrderedTags = new();

    public string Module { get; }
    public string Name { get; }

    public NetfoxLogger(string module, string name)
    {
        Module = module;
        Name = name;
    }

    public static NetfoxLogger ForNetfox(string name) => new("netfox", name);
    public static NetfoxLogger ForExtras(string name) => new("netfox.extras", name);

    public static void RegisterTag(Func<string> tag, int priority = 0)
    {
        if (!Tags.TryGetValue(priority, out var group))
            Tags[priority] = group = new List<Func<string>>();
        group.Add(tag);
        RebuildOrderedTags();
    }

    public static void FreeTag(Func<string> tag)
    {
        foreach (var priority in Tags.Keys.ToList())
        {
            var group = Tags[priority];
            group.Remove(tag);
            if (group.Count == 0) Tags.Remove(priority);
        }
        OrderedTags.Remove(tag);
    }

    private static void RebuildOrderedTags()
    {
        OrderedTags.Clear();
        foreach (var group in Tags.Values)
            OrderedTags.AddRange(group);
    }

    public void Trace(string text, params object?[] values) => Log(text, values, LogLevel.Trace);
    public void Debug(string text, params object?[] values) => Log(text, values, LogLevel.Debug);
    public void Info(string text, params object?[] values) => Log(text, values, LogLevel.Info);

    public void Warning(string text, params object?[] values)
    {
        if (!CheckLevel(LogLevel.Warn)) return;
        var formatted = Format(text, values, LogLevel.Warn);
        if (PushToDebugger) PushWarning(formatted);
        Print(formatted);
    }

    public void Error(string text, params object?[] values)
    {
        if (!CheckLevel(LogLevel.Error)) return;
        var formatted = Format(text, values, LogLevel.Error);
        if (PushToDebugger) PushError(formatted);
        Print(formatted);
    }

    public bool CheckLevel(LogLevel level)
    {
        if (level < Level) return false;
        if (ModuleLevels.TryGetValue(Module, out var moduleLevel))
            return level >= moduleLevel;
        return true;
    }

    private string Format(string text, object?[] values, LogLevel level)
    {
        level = (LogLevel)Math.Clamp((int)level, (int)LogLevel.Trace, (int)LogLevel.Error);
        var sb = new System.Text.StringBuilder();
        sb.Append('[').Append(LevelPrefixes[(int)level]).Append(']');
        foreach (var tag in OrderedTags)
            sb.Append('[').Append(tag()).Append(']');
        sb.Append('[').Append(Module).Append("::").Append(Name).Append("] ");
        sb.Append(values.Length == 0 ? text : string.Format(text, values));
        return sb.ToString();
    }

    private void Log(string text, object?[] values, LogLevel level)
    {
        if (CheckLevel(level)) Print(Format(text, values, level));
    }
}
