using Godot;

namespace CrackNet.Internal;

/// <summary>
/// Where this peer drew an object over the last frames, by display tick.
/// <para>
/// A character standing on a moving body measures its offset against its own copy of that body, which trails the
/// body's authority by the depth of its link. Every other peer places it on a copy of its own, at the time they show
/// everything else, and the two are apart by what the body travelled in between: the rider is drawn jumping along
/// the body as it steps on, and back as it steps off. Its samples say how far behind its copy was, and this history
/// says where the copy here stood then, so both places - where the rider was in the world, and where it sits on the
/// body - are known at every tick and the switch crosses between them instead of stepping across.
/// </para>
/// </summary>
internal sealed class DrawnHistory
{
    /// <summary>Frames kept: enough to reach back past the deepest playback a peer runs.</summary>
    private const int Capacity = 128;

    private readonly (double Tick, Transform3D At)[] _frames = new (double, Transform3D)[Capacity];
    private int _next, _count;

    public void Record(double tick, Transform3D at)
    {
        var last = (_next - 1 + Capacity) % Capacity;
        // A frame that did not advance the display tick replaces the one before it rather than filling the history
        if (_count > 0 && _frames[last].Tick >= tick)
        {
            _frames[last] = (tick, at);
            return;
        }
        _frames[_next] = (tick, at);
        _next = (_next + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    /// <summary>Where the object was drawn at <paramref name="tick"/>, or null when the history does not reach back that far.</summary>
    public Transform3D? At(double tick)
    {
        for (var i = 1; i < _count; i++)
        {
            var newer = _frames[(_next - i + Capacity) % Capacity];
            var older = _frames[(_next - i - 1 + Capacity) % Capacity];
            if (older.Tick > tick) continue;
            if (newer.Tick <= tick) return newer.At;
            var span = newer.Tick - older.Tick;
            return span <= 0 ? older.At : older.At.InterpolateWith(newer.At, (float)((tick - older.Tick) / span));
        }
        return null;
    }

    public void Clear()
    {
        _count = 0;
        _next = 0;
    }
}
