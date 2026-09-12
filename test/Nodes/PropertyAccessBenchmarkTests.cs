using System.Diagnostics;
using Godot;

namespace Netfox.Tests;

/// <summary>
/// Not a check, a measurement: how the ways of reading and writing a property compare, since record and restore do this
/// for every property of every subject, every tick. Prints its numbers; only fails if the cached path is slower.
/// </summary>
public partial class PropertyAccessBenchmarkTests : TestSuite
{
    private const int Iterations = 200_000;

    [Test]
    public async Task CompareAccessPaths()
    {
        var node = await Mount(new Node3D { Name = "Access Subject" });
        var path = new NodePath("position");
        var name = new StringName("position");

        // Warm up the paths so the first call's setup does not land in the measurement
        for (var i = 0; i < 1000; i++)
        {
            node.SetIndexed(path, Vector3.One);
            _ = node.GetIndexed(path);
            node.Set(name, Vector3.One);
            _ = node.Get(name);
            node.Position = Vector3.One;
            _ = node.Position;
        }

        var indexed = Measure(() =>
        {
            var value = node.GetIndexed(path);
            node.SetIndexed(path, value);
        });

        var direct = Measure(() =>
        {
            var value = node.Get(name);
            node.Set(name, value);
        });

        var typed = Measure(() =>
        {
            var value = node.Position;
            node.Position = value;
        });

        // What a shared cache costs: the call sites that only have a NodePath have to look the StringName up
        var cache = new Dictionary<NodePath, StringName> { [path] = name };
        var cached = Measure(() =>
        {
            var resolved = cache[path];
            var value = node.Get(resolved);
            node.Set(resolved, value);
        });

        var line = FormattableString.Invariant(
            $"PROPERTY ACCESS over {Iterations} get+set pairs: GetIndexed/SetIndexed {indexed:F1}ms, Get/Set {direct:F1}ms");
        var line2 = FormattableString.Invariant($"cached lookup + Get/Set {cached:F1}ms, C# property {typed:F1}ms");
        GD.Print($"{line}, {line2}");

        // What PropertyAccess does: resolve the name once, keep it in a dictionary, then Get and Set through it
        Expect.True(cached < indexed * 0.6,
            $"the cached name path ({cached:F1}ms) should stay well under the indexed one ({indexed:F1}ms)");
    }

    private static double Measure(Action body)
    {
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < Iterations; i++) body();
        return stopwatch.Elapsed.TotalMilliseconds;
    }
}
