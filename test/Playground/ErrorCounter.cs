using System.Runtime.CompilerServices;
using Godot;
using Godot.Collections;

namespace CrackNet.Examples.Playground;

/// <summary>
/// Counts the errors Godot logs, script exceptions included: an exception in a callback is logged and swallowed, so a
/// smoke that only checks its own measurements passed with twelve of them at every start.
/// </summary>
public partial class ErrorCounter : Logger
{
    /// <summary>Counting from the moment the assembly loads, before any scene node is ready; only for a smoke run.</summary>
    public static ErrorCounter? Smoke { get; private set; }

#pragma warning disable CA2255   // a module initializer is exactly what catches the errors of the first frame
    [ModuleInitializer]
    internal static void Register()
    {
        if (!OS.GetCmdlineUserArgs().Contains("--smoke")) return;
        Smoke = new ErrorCounter();
        OS.AddLogger(Smoke);
    }
#pragma warning restore CA2255

    private int _count;
    private string _first = "";

    public int Count => _count;
    public string First => _first;

    public override void _LogError(string function, string file, int line, string code, string rationale,
        bool editorNotify, int errorType, Array<ScriptBacktrace> scriptBacktraces)
    {
        if (errorType == (int)ErrorType.Warning || errorType == (int)ErrorType.Shader) return;
        // Loggers are called from any thread
        if (Interlocked.Increment(ref _count) == 1) _first = $"{file}:{line} {code} {rationale}";
    }
}
