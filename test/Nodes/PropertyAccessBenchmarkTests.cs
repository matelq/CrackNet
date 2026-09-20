using System.Diagnostics;
using CrackNet.Internal;
using Godot;

namespace CrackNet.Tests;

/// <summary>
/// Not a check, a measurement: how the ways of reading and writing a property compare, since record and restore do this
/// for every property of every subject, every tick. Prints its numbers; only fails if the cached path is slower.
/// <para>
/// The two paths it compares are measured in alternating rounds, and the verdict is the median of the per-round
/// ratios. One measurement each was not enough on shared CI: a round that lands on a busy moment charges whichever
/// path it hit, and 65.5ms against 80.8ms failed a run that was not a regression. Interleaving puts the same bad
/// moment in both halves of a round, and the median throws the round away. There is nothing steadier to count here:
/// the path parse the cache saves happens in Godot, so both paths allocate zero managed bytes.
/// </para>
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

        var (indexed, throughCache, ratio) = MeasureAgainstEachOther(
            () =>
            {
                var value = node.GetIndexed(path);
                node.SetIndexed(path, value);
            },
            () =>
            {
                var value = node.GetValue(path);
                node.SetValue(path, value);
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

        var line = FormattableString.Invariant(
            $"PROPERTY ACCESS over {Iterations} get+set pairs: GetIndexed/SetIndexed {indexed:F1}ms, Get/Set {direct:F1}ms");
        var line2 = FormattableString.Invariant(
            $"PropertyAccess cache {throughCache:F1}ms (median ratio {ratio:F2}), C# property {typed:F1}ms");
        GD.Print($"{line}, {line2}");

        // Guards the optimization itself: the CrackNet path has to stay clear of the indexed one. 0.41 locally, and
        // the margin is generous because even a median of interleaved rounds carries CI's noise.
        Expect.True(ratio < 0.8,
            $"the PropertyAccess cache ({throughCache:F1}ms) should stay clear of GetIndexed/SetIndexed " +
            $"({indexed:F1}ms): median ratio {ratio:F2} over {Rounds} rounds");
    }

    private const int Rounds = 5;

    /// <summary>
    /// Runs the two bodies in alternating rounds of the same size and returns each one's total and the median of the
    /// per-round ratios, which is the number a verdict can be built on.
    /// </summary>
    private static (double First, double Second, double Ratio) MeasureAgainstEachOther(Action first, Action second)
    {
        var ratios = new List<double>();
        double firstTotal = 0, secondTotal = 0;

        for (var round = 0; round < Rounds; round++)
        {
            var a = Measure(first, Iterations / Rounds);
            var b = Measure(second, Iterations / Rounds);
            firstTotal += a;
            secondTotal += b;
            ratios.Add(b / a);
        }

        ratios.Sort();
        return (firstTotal, secondTotal, ratios[Rounds / 2]);
    }

    private static double Measure(Action body) => Measure(body, Iterations);

    private static double Measure(Action body, int iterations)
    {
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) body();
        return stopwatch.Elapsed.TotalMilliseconds;
    }
}
