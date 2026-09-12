using Godot;

namespace Netfox.Extras;

/// <summary>
/// Random numbers that are reproducible per tick: the same tick always yields the same sequence, so resimulated ticks agree.
/// Port of netfox.extras/rewindable-random-number-generator.gd.
/// </summary>
public sealed class RewindableRandomNumberGenerator
{
    private readonly RandomNumberGenerator _rng = new();
    private readonly long _seed;
    private int _lastResetTick = -1;
    private int _lastResetRollbackTick = -1;

    public RewindableRandomNumberGenerator(long seed)
    {
        _seed = seed;
        _rng.Seed = (ulong)seed;
    }

    public float Randf()
    {
        EnsureState();
        return _rng.Randf();
    }

    public float RandfRange(float from, float to)
    {
        EnsureState();
        return _rng.RandfRange(from, to);
    }

    public float Randfn(float mean = 0.0f, float deviation = 1.0f)
    {
        EnsureState();
        return _rng.Randfn(mean, deviation);
    }

    public uint Randi()
    {
        EnsureState();
        return _rng.Randi();
    }

    public int RandiRange(int from, int to)
    {
        EnsureState();
        return _rng.RandiRange(from, to);
    }

    private void EnsureState()
    {
        var networkTick = NetworkTime.Instance?.Tick ?? 0;
        var rollback = NetworkRollback.Instance;
        var rollbackTick = rollback?.Tick ?? 0;

        if (networkTick == _lastResetTick && rollbackTick == _lastResetRollbackTick) return;

        var tick = rollback is not null && rollback.IsRollback() ? rollbackTick : networkTick;
        _rng.Seed = unchecked((ulong)HashCode.Combine(_seed, tick));

        _lastResetRollbackTick = rollbackTick;
        _lastResetTick = networkTick;
    }
}
