using System.Reflection;
using Godot;

namespace CrackNet.Tests;

/// <summary>Marks a test method on a TestSuite. Methods may return void or Task.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute { }

public sealed class TestFailedException(string message) : Exception(message);

/// <summary>Minimal assertions; every failure throws TestFailedException with a readable message.</summary>
public static class Expect
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition) throw new TestFailedException(message ?? "Expected true");
    }

    public static void False(bool condition, string? message = null)
    {
        if (condition) throw new TestFailedException(message ?? "Expected false");
    }

    public static void Null(object? value, string? message = null)
    {
        if (value is not null) throw new TestFailedException(message ?? $"Expected null, got {value}");
    }

    public static void NotNull(object? value, string? message = null)
    {
        if (value is null) throw new TestFailedException(message ?? "Expected non-null");
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new TestFailedException(message ?? $"Expected {Format(expected)}, got {Format(actual)}");
    }

    public static void NotEqual<T>(T unexpected, T actual, string? message = null)
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
            throw new TestFailedException(message ?? $"Did not expect {Format(actual)}");
    }

    public static void VariantEqual(Variant expected, Variant actual, string? message = null)
    {
        if (!Internal.VariantComparer.Instance.Equals(expected, actual))
            throw new TestFailedException(message ?? $"Expected {expected}, got {actual}");
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? message = null)
    {
        var e = expected.ToList();
        var a = actual.ToList();
        if (!e.SequenceEqual(a))
            throw new TestFailedException(message ?? $"Expected [{string.Join(", ", e)}], got [{string.Join(", ", a)}]");
    }

    public static void Empty<T>(IEnumerable<T> actual, string? message = null)
    {
        var a = actual.ToList();
        if (a.Count != 0) throw new TestFailedException(message ?? $"Expected empty, got [{string.Join(", ", a)}]");
    }

    public static void Approx(double expected, double actual, double tolerance = 1e-4, string? message = null)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new TestFailedException(message ?? $"Expected {expected} within {tolerance}, got {actual}");
    }

    private static string Format<T>(T value) => value is IEnumerable<object> e ? $"[{string.Join(", ", e)}]" : value?.ToString() ?? "null";
}

/// <summary>Base for Godot-side test suites. Override BeforeCase/AfterCase; add test methods with [Test].</summary>
public abstract partial class TestSuite : Node
{
    public virtual Task BeforeCase() => Task.CompletedTask;
    public virtual Task AfterCase() => Task.CompletedTask;

    /// <summary>Adds <paramref name="node"/> under the test suite and waits until it is ready.</summary>
    protected async Task<T> Mount<T>(T node) where T : Node
    {
        AddChild(node);
        if (!node.IsNodeReady()) await ToSignal(node, Node.SignalName.Ready);
        return node;
    }

    protected async Task NextFrame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    /// <summary>
    /// Pumps frames until <paramref name="condition"/> holds or <paramref name="seconds"/> of wall clock pass. CrackNet
    /// ticks on real time, so tests that need ticks have to wait on the clock, not on a frame count.
    /// </summary>
    protected async Task<bool> WaitUntil(Func<bool> condition, double seconds = 3)
    {
        var deadline = Time.GetTicksMsec() + (ulong)(seconds * 1000);
        while (!condition())
        {
            if (Time.GetTicksMsec() > deadline) return false;
            await NextFrame();
        }
        return true;
    }

    /// <summary>Frees every child added during a case.</summary>
    protected void FreeChildren()
    {
        foreach (var child in GetChildren())
        {
            RemoveChild(child);
            child.Free();
        }
    }
}

/// <summary>
/// Discovers every TestSuite in the assembly, runs its [Test] methods, prints a summary and quits with exit code 0 or 1.
/// Run headless: godot --headless --path . res://test/TestRunner.tscn
/// </summary>
public partial class TestRunner : Node
{
    public override async void _Ready()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var passed = 0;
        var failed = 0;
        var failures = new List<string>();

        // -- --test=Suite or --test=Suite.Case (repeatable): run only those, for a quick check or a mutant
        var filters = OS.GetCmdlineUserArgs().Where(arg => arg.StartsWith("--test=")).Select(arg => arg["--test=".Length..]).ToList();
        bool Selected(string suite, string test)
            => filters.Count == 0 || filters.Any(filter => filter == suite || filter == $"{suite}.{test}");

        var suites = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(TestSuite)))
            .OrderBy(t => t.Name);

        foreach (var suiteType in suites)
        {
            var tests = suiteType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.GetCustomAttribute<TestAttribute>() is not null && Selected(suiteType.Name, m.Name))
                .ToList();
            if (tests.Count == 0) continue;

            GD.Print($"== {suiteType.Name}");
            foreach (var test in tests)
            {
                var suite = (TestSuite)Activator.CreateInstance(suiteType)!;
                suite.Name = suiteType.Name;
                AddChild(suite);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

                try
                {
                    // Cases share the autoload servers, so a case that ran ticks would leave the history buffers
                    // ahead of where the next case starts, and its writes would be dropped as out of window
                    CrackNetContext.Default.ResetSession();
                    await suite.BeforeCase();
                    var result = test.Invoke(suite, null);
                    if (result is Task task) await task;
                    GD.Print($"   ok   {test.Name}");
                    passed++;
                }
                catch (Exception e)
                {
                    var inner = e is TargetInvocationException { InnerException: { } i } ? i : e;
                    var reason = inner is TestFailedException ? inner.Message : inner.ToString();
                    GD.Print($"   FAIL {test.Name}: {reason}");
                    failures.Add($"{suiteType.Name}.{test.Name}: {reason}");
                    failed++;
                }
                finally
                {
                    try { await suite.AfterCase(); } catch (Exception e) { GD.Print($"   (after_case error: {e.Message})"); }
                    RemoveChild(suite);
                    suite.Free();
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }
            }
        }

        if (filters.Count > 0 && passed + failed == 0)
        {
            GD.Print($"No test matched {string.Join(", ", filters)}");
            GetTree().Quit(1);
            return;
        }

        GD.Print($"\n{passed} passed, {failed} failed");
        foreach (var failure in failures) GD.Print($"  - {failure}");
        GetTree().Quit(failed == 0 ? 0 : 1);
    }
}
