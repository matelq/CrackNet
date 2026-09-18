using CrackNet.Core.Logging;
using Godot;

namespace CrackNet.Internal;

/// <summary>
/// Draws <see cref="NetworkObject.Visual"/> where it was on screen when the object changes hands and lets it catch up
/// with the body, instead of jumping with it. The body itself moves at once: only the drawing lags.
/// <para>
/// A handover moves the object on every peer, but not at the same moment: the peer that takes it jumps to the newest
/// sample at once, the others when playback reaches the new authority's first one. So a change opens a window, and
/// within it any frame whose body moved further than its speed explains adds that unexplained part to an offset the
/// visual keeps; the offset then fades over <see cref="NetworkObject.SmoothingTime"/>
/// (https://gafferongames.com/post/state_synchronization/, "visual smoothing"). Outside the window nothing is touched,
/// so a game's own motion of the visual is left alone.
/// </para>
/// </summary>
internal sealed class AuthorityChangeSmoothing
{
    private static readonly CrackNetLogger Logger = CrackNetLogger.ForCrackNet("AuthorityChangeSmoothing");

    // Long enough for an observer to reach the new authority's samples: its ping and playback delay
    private const double WindowSeconds = 1;
    // Below this, a frame's unexplained motion is interpolation noise rather than a handover
    private const float Noise = 0.01f;

    private readonly NetworkObject _object;
    private readonly Node3D _root;
    private readonly Node3D _visual;
    private readonly Transform3D _rest;

    private Transform3D? _lastBody;
    private Vector3 _lastVelocity;
    private Vector3 _offset;
    private Quaternion _rotationOffset = Quaternion.Identity;
    private double _window;
    private bool _snapped;
    private bool _drawnAway;

    private AuthorityChangeSmoothing(NetworkObject obj, Node3D root, Node3D visual)
    {
        _object = obj;
        _root = root;
        _visual = visual;
        _rest = visual.Transform;
    }

    public static AuthorityChangeSmoothing? For(NetworkObject obj)
    {
        if (obj.Visual is not { } visual || obj.Root is not Node3D root) return null;
        if (NetworkObject.VisualProblem(root, visual) is { } problem)
        {
            Logger.Error("{0}: {1}. Smoothing is off", obj.GetPath(), problem);
            return null;
        }
        return new AuthorityChangeSmoothing(obj, root, visual);
    }

    /// <summary>The object changed hands here: the next second's jumps are the handover's.</summary>
    public void Opened() => _window = WindowSeconds;

    /// <summary>A snap is meant to be seen: the next jump is not smoothed, and what was being smoothed is dropped.</summary>
    public void Snapped() => _snapped = true;

    /// <summary>Once per rendered frame, after playback has placed the body.</summary>
    public void Process(double delta)
    {
        var body = _root.GlobalTransform;
        var velocity = _root switch
        {
            RigidBody3D rigid => rigid.LinearVelocity,
            CharacterBody3D character => character.Velocity,
            _ => Vector3.Zero,
        };

        if (_lastBody is { } last && _window > 0)
        {
            // What the body's own speed does not explain this frame; averaged, so a bounce is not taken for a jump
            var unexplained = body.Origin - (last.Origin + (_lastVelocity + velocity) / 2 * (float)delta);
            if (_snapped || unexplained.Length() > _object.MaxSmoothingDistance)
            {
                _offset = Vector3.Zero;
                _rotationOffset = Quaternion.Identity;
            }
            else if (unexplained.Length() > Noise)
            {
                _offset -= unexplained;
                // Drawn turned as before (offset' * body = offset * last). Rotation has no speed of its own here: a
                // handover turn is the whole step, and a steady spin is small per frame next to it
                _rotationOffset = _rotationOffset
                                  * (last.Basis.GetRotationQuaternion() * body.Basis.GetRotationQuaternion().Inverse())
                                  .Normalized();
            }
        }
        _snapped = false;
        _lastBody = body;
        _lastVelocity = velocity;
        _window = Math.Max(0, _window - delta);

        // Fade: the same share of what is left each frame, whatever the frame rate
        var keep = _object.SmoothingTime > 0 ? (float)Math.Exp(-delta * 3 / _object.SmoothingTime) : 0;
        _offset *= keep;
        _rotationOffset = Quaternion.Identity.Slerp(_rotationOffset.Normalized(), keep);
        if (_offset.LengthSquared() < 1e-6f && _rotationOffset.IsEqualApprox(Quaternion.Identity))
        {
            _offset = Vector3.Zero;
            _rotationOffset = Quaternion.Identity;
            if (_drawnAway) _visual.Transform = _rest;
            _drawnAway = false;
            return;
        }

        var drawn = _visual.GetParentNode3D().GlobalTransform * _rest;
        _visual.GlobalTransform = new Transform3D(new Basis(_rotationOffset) * drawn.Basis, drawn.Origin + _offset);
        _drawnAway = true;
    }
}
