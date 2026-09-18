using System.Diagnostics;
using CrackNet.Internal;
using Godot;

namespace CrackNet.Tests;

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

        // What CrackNet actually uses: PropertyAccess keeps a shared cache
        var throughCache = Measure(() =>
        {
            var value = node.GetValue(path);
            node.SetValue(path, value);
        });

        var line = FormattableString.Invariant(
            $"PROPERTY ACCESS over {Iterations} get+set pairs: GetIndexed/SetIndexed {indexed:F1}ms, Get/Set {direct:F1}ms");
        var line2 = FormattableString.Invariant(
            $"PropertyAccess cache {throughCache:F1}ms, C# property {typed:F1}ms");
        GD.Print($"{line}, {line2}");

        // Guards the optimization itself: the CrackNet path has to stay clear of the indexed one. The margin is
        // generous because this runs on shared CI hardware, where the ratio is nearer 0.6 than the 0.3 seen locally.
        Expect.True(throughCache < indexed * 0.8,
            $"the PropertyAccess cache ({throughCache:F1}ms) should stay clear of GetIndexed/SetIndexed ({indexed:F1}ms)");
    }

    private static double Measure(Action body)
    {
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < Iterations; i++) body();
        return stopwatch.Elapsed.TotalMilliseconds;
    }
}
