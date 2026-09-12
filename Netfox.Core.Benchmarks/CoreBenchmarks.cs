using BenchmarkDotNet.Attributes;
using Netfox.Core.Collections;
using Netfox.Core.Data;
using Netfox.Core.Serialization;

namespace Netfox.Core.Benchmarks;

/// <summary>Stand-in for a node: snapshots only ever use subjects as dictionary keys.</summary>
public sealed class Subject(string name)
{
    public string Name { get; } = name;
    public override string ToString() => Name;
}

/// <summary>The ring buffer every server keeps per tick; reads dominate, since rollback walks it every loop.</summary>
[MemoryDiagnoser]
public class HistoryBufferBenchmarks
{
    private HistoryBuffer<int> _buffer = null!;

    [Params(64)] public int Capacity { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _buffer = new HistoryBuffer<int>(Capacity);
        for (var tick = 0; tick < Capacity; tick++)
            _buffer.SetAt(tick, tick);
    }

    [Benchmark]
    public int SetAtAndRead()
    {
        _buffer.SetAt(Capacity, Capacity);
        return _buffer.GetAt(Capacity);
    }

    [Benchmark]
    public int GetLatestAtMiss() => _buffer.GetLatestIndexAt(Capacity / 2 + 1);

    [Benchmark]
    public HistoryBuffer<int> Duplicate() => _buffer.Duplicate();
}

/// <summary>Snapshots are merged on every received packet and diffed against the baseline on every sent one.</summary>
[MemoryDiagnoser]
public class SnapshotBenchmarks
{
    private Snapshot<Subject, string, int> _baseline = null!;
    private Snapshot<Subject, string, int> _incoming = null!;

    [Params(8, 64)] public int Properties { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var subjects = Enumerable.Range(0, 4).Select(i => new Subject($"Subject{i}")).ToList();

        _baseline = new Snapshot<Subject, string, int>(1);
        _incoming = new Snapshot<Subject, string, int>(2);
        foreach (var subject in subjects)
            for (var i = 0; i < Properties; i++)
            {
                _baseline.SetProperty(subject, $"property{i}", i);
                // Half the properties differ, which is what a diff state ends up carrying
                _incoming.SetProperty(subject, $"property{i}", i % 2 == 0 ? i : i + 1);
            }
    }

    [Benchmark]
    public bool Merge() => _baseline.Duplicate().Merge(_incoming);

    [Benchmark]
    public Snapshot<Subject, string, int> MakePatch()
        => Snapshot<Subject, string, int>.MakePatch(_baseline, _incoming);

    [Benchmark]
    public Snapshot<Subject, string, int> Duplicate() => _baseline.Duplicate();
}

/// <summary>The primitives under every packet netfox sends.</summary>
[MemoryDiagnoser]
public class SerializationBenchmarks
{
    private readonly ByteWriter _writer = new(1024);
    private byte[] _varuints = null!;
    private Bitset _bitset = null!;

    [GlobalSetup]
    public void Setup()
    {
        var writer = new ByteWriter(1024);
        for (var i = 0; i < 128; i++)
            VarUint.Encode((ulong)(i * 977), writer);
        _varuints = writer.ToArray();

        _bitset = new Bitset(128);
        for (var i = 0; i < 128; i += 3)
            _bitset.SetBit(i);
    }

    [Benchmark]
    public int EncodeVarUints()
    {
        _writer.Clear();
        for (var i = 0; i < 128; i++)
            VarUint.Encode((ulong)(i * 977), _writer);
        return _writer.Size;
    }

    [Benchmark]
    public ulong DecodeVarUints()
    {
        var reader = new ByteReader(_varuints);
        ulong sum = 0;
        for (var i = 0; i < 128; i++)
            sum += VarUint.Decode(reader);
        return sum;
    }

    [Benchmark]
    public int EncodeBitset()
    {
        _writer.Clear();
        VarBits.Encode(_bitset, _writer);
        return _writer.Size;
    }
}

/// <summary>Upstream benchmarks this one too (test/netfox.internals/graph.perf.gd); rollback walks it per tick.</summary>
[MemoryDiagnoser]
public class GraphBenchmarks
{
    private Graph<Subject> _graph = null!;
    private List<Subject> _subjects = null!;

    [Params(32)] public int Nodes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _subjects = Enumerable.Range(0, Nodes).Select(i => new Subject($"Subject{i}")).ToList();
        _graph = new Graph<Subject>();
        for (var i = 1; i < _subjects.Count; i++)
            _graph.Link(_subjects[i - 1], _subjects[i]);
    }

    [Benchmark]
    public int LinkAndUnlink()
    {
        _graph.Link(_subjects[0], _subjects[^1]);
        _graph.Unlink(_subjects[0], _subjects[^1]);
        return _graph.GetLinkedFrom(_subjects[0]).Count;
    }

    [Benchmark]
    public int Traverse()
    {
        var total = 0;
        foreach (var subject in _subjects)
            total += _graph.GetLinkedFrom(subject).Count + _graph.GetLinkedTo(subject).Count;
        return total;
    }
}
