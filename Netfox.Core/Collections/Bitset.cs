namespace Netfox.Core.Collections;

/// <summary>Stores a list of booleans packed into bytes. Port of netfox.internals/bitset.gd.</summary>
public sealed class Bitset : IEquatable<Bitset>
{
    private readonly byte[] _data;
    private readonly int _bitCount;

    public Bitset(int bitCount)
    {
        _bitCount = bitCount;
        _data = new byte[(bitCount + 7) / 8];
    }

    public Bitset(int bitCount, byte[] data)
    {
        if (data.Length != (bitCount + 7) / 8)
            throw new ArgumentException($"Expected {(bitCount + 7) / 8} bytes for {bitCount} bits, got {data.Length}");
        _bitCount = bitCount;
        _data = data;
    }

    public static Bitset OfBools(IReadOnlyList<bool> values)
    {
        var result = new Bitset(values.Count);
        for (var i = 0; i < values.Count; i++)
            if (values[i]) result.SetBit(i);
        return result;
    }

    public int BitCount => _bitCount;
    public bool IsEmpty => _bitCount == 0;
    public bool IsNotEmpty => _bitCount != 0;
    public ReadOnlySpan<byte> Bytes => _data;

    public bool GetBit(int idx)
    {
        Check(idx);
        return ((_data[idx / 8] >> (idx % 8)) & 0x1) != 0;
    }

    public void SetBit(int idx)
    {
        Check(idx);
        _data[idx / 8] |= (byte)(0x1 << (idx % 8));
    }

    public void ClearBit(int idx)
    {
        Check(idx);
        _data[idx / 8] &= (byte)~(0x1 << (idx % 8));
    }

    public void ToggleBit(int idx)
    {
        Check(idx);
        _data[idx / 8] ^= (byte)(0x1 << (idx % 8));
    }

    public List<int> GetSetIndices()
    {
        var result = new List<int>();
        for (var i = 0; i < _data.Length; i++)
        {
            var b = _data[i];
            for (var bit = 0; bit < 8; bit++)
                if ((b & (1 << bit)) != 0) result.Add(i * 8 + bit);
        }
        return result;
    }

    public bool Equals(Bitset? other)
        => other is not null && other._bitCount == _bitCount && other._data.AsSpan().SequenceEqual(_data);

    public override bool Equals(object? obj) => Equals(obj as Bitset);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_bitCount);
        hash.AddBytes(_data);
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        if (IsEmpty) return "Bitset(n=0)";
        var body = new System.Text.StringBuilder();
        for (var i = 0; i < _bitCount; i++)
        {
            if (i != 0 && i % 4 == 0) body.Append(' ');
            body.Append(GetBit(i) ? '1' : '0');
        }
        return $"Bitset(n={_bitCount}, {body})";
    }

    private void Check(int idx)
    {
        if ((uint)idx >= (uint)_bitCount)
            throw new ArgumentOutOfRangeException(nameof(idx), $"Accessing bit {idx} on bitset of size {_bitCount}!");
    }
}
