using Godot;
using Netfox.Core.Logging;

namespace Netfox.Internal;

/// <summary>
/// What the library does for a physics root so a game does not have to: freeze it where another peer simulates it,
/// pass authority on contact, and hand a settled shared body back to the host.
/// </summary>
internal abstract class PhysicsHandling
{
    private static readonly NetfoxLogger Logger = NetfoxLogger.ForNetfox("PhysicsHandling");

    protected readonly NetworkObject Object;

    protected PhysicsHandling(NetworkObject obj) => Object = obj;

    public static PhysicsHandling? For(NetworkObject obj) => obj.Root switch
    {
        RigidBody3D body => new Rigid3D(obj, body),
        RigidBody2D body => new Rigid2D(obj, body),
        CharacterBody3D body => new Character3D(obj, body),
        CharacterBody2D body => new Character2D(obj, body),
        _ => null,
    };

    public virtual void AuthorityChanged() { }
    public abstract void PhysicsProcess();

    protected bool IsHost => Object.Root!.Multiplayer.IsServer();

    /// <summary>Passes authority to whatever object <paramref name="collider"/> is the root of.</summary>
    protected void TouchCollider(GodotObject? collider)
    {
        if (collider is Node node && NetworkObject.Of(node) is { } other) Object.Touch(other);
    }

    /// <summary>A body moving slower than this does not pass authority to what it bumps: a settling stack stays put.</summary>
    protected const float TouchSpeed = 0.5f;

    /// <summary>Physics frames a shared body has to rest before it goes back to the host.</summary>
    protected const int RestFramesBeforeReturning = 30;

    protected const float RestSpeed = 0.05f;

    /// <summary>
    /// Rapier keeps a body's last kinematic target and returns to it on the next freeze: set the transform again after
    /// the switch, or a body handed back after a throw jumps to where it was held.
    /// </summary>
    internal static void SetFrozen(RigidBody3D body, bool frozen)
    {
        if (body.Freeze == frozen) return;
        var transform = body.GlobalTransform;
        body.Freeze = frozen;
        body.GlobalTransform = transform;
        PhysicsServer3D.BodySetState(body.GetRid(), PhysicsServer3D.BodyState.Transform, transform);
    }

    private sealed class Rigid3D : PhysicsHandling
    {
        private readonly RigidBody3D _body;
        private int _restFrames;

        public Rigid3D(NetworkObject obj, RigidBody3D body) : base(obj)
        {
            _body = body;
            body.FreezeMode = RigidBody3D.FreezeModeEnum.Kinematic;
            body.ContactMonitor = true;
            body.MaxContactsReported = Math.Max(body.MaxContactsReported, 4);
            body.BodyEntered += other =>
            {
                if (Object.IsAuthority && _body.LinearVelocity.Length() > TouchSpeed) TouchCollider(other);
            };
        }

        public override void AuthorityChanged()
        {
            SetFrozen(_body, !Object.IsAuthority || Object.Holder != 0);
            // Whoever takes a body takes what rests on and against it: a frozen body reports no resting contacts, and
            // left alone a stack would hang in the air here until the host's word that it fell
            if (Object.IsAuthority && !IsHost) TouchOverlapping();
        }

        private void TouchOverlapping()
        {
            if (!_body.IsInsideTree()) return;
            var space = _body.GetWorld3D().DirectSpaceState;
            foreach (var shape in _body.GetChildren().OfType<CollisionShape3D>())
            {
                if (shape.Shape is null || shape.Disabled) continue;
                var query = new PhysicsShapeQueryParameters3D
                {
                    Shape = shape.Shape,
                    Transform = shape.GlobalTransform,
                    Margin = 0.05f,
                    CollisionMask = _body.CollisionMask,
                    Exclude = [_body.GetRid()],
                };
                var hits = space.IntersectShape(query, 16);
                Logger.Debug("{0} taken at {1}: overlap query found {2}", _body.Name, _body.GlobalPosition,
                    hits.Count == 0 ? "nothing" : string.Join(", ", hits.Select(hit => hit["collider"].AsGodotObject() is Node node ? $"{node.Name}" : "?")));
                foreach (var hit in hits)
                {
                    var collider = hit["collider"].AsGodotObject();
                    if (collider is not Node node || NetworkObject.Of(node) is not { } other) continue;
                    var touched = Object.Touch(other);
                    Logger.Debug("{0} touches {1}: {2}", _body.Name, node.Name, touched);
                }
            }
        }

        public override void PhysicsProcess()
        {
            if (Object.ResolvedKind != NetworkObject.ObjectKind.Shared || !Object.IsAuthority || Object.Holder != 0 || IsHost)
            {
                _restFrames = 0;
                return;
            }
            _restFrames = _body.Sleeping || _body.LinearVelocity.Length() < RestSpeed ? _restFrames + 1 : 0;
            if (_restFrames >= RestFramesBeforeReturning && Object.ReturnToHost()) _restFrames = 0;
        }
    }

    private sealed class Rigid2D : PhysicsHandling
    {
        private readonly RigidBody2D _body;
        private int _restFrames;

        public Rigid2D(NetworkObject obj, RigidBody2D body) : base(obj)
        {
            _body = body;
            body.FreezeMode = RigidBody2D.FreezeModeEnum.Kinematic;
            body.ContactMonitor = true;
            body.MaxContactsReported = Math.Max(body.MaxContactsReported, 4);
            body.BodyEntered += other =>
            {
                if (Object.IsAuthority && _body.LinearVelocity.Length() > TouchSpeed) TouchCollider(other);
            };
        }

        public override void AuthorityChanged()
        {
            var frozen = !Object.IsAuthority || Object.Holder != 0;
            if (_body.Freeze != frozen)
            {
                var transform = _body.GlobalTransform;
                _body.Freeze = frozen;
                _body.GlobalTransform = transform;
                PhysicsServer2D.BodySetState(_body.GetRid(), PhysicsServer2D.BodyState.Transform, transform);
            }
            if (!Object.IsAuthority || IsHost || !_body.IsInsideTree()) return;

            var space = _body.GetWorld2D().DirectSpaceState;
            foreach (var shape in _body.GetChildren().OfType<CollisionShape2D>())
            {
                if (shape.Shape is null || shape.Disabled) continue;
                var query = new PhysicsShapeQueryParameters2D
                {
                    Shape = shape.Shape,
                    Transform = shape.GlobalTransform,
                    Margin = 0.5f,
                    CollisionMask = _body.CollisionMask,
                    Exclude = [_body.GetRid()],
                };
                foreach (var hit in space.IntersectShape(query, 16))
                    TouchCollider(hit["collider"].AsGodotObject());
            }
        }

        public override void PhysicsProcess()
        {
            if (Object.ResolvedKind != NetworkObject.ObjectKind.Shared || !Object.IsAuthority || Object.Holder != 0 || IsHost)
            {
                _restFrames = 0;
                return;
            }
            _restFrames = _body.Sleeping || _body.LinearVelocity.Length() < RestSpeed ? _restFrames + 1 : 0;
            if (_restFrames >= RestFramesBeforeReturning && Object.ReturnToHost()) _restFrames = 0;
        }
    }

    /// <summary>A character body passes authority to what it slid into this frame; its own movement is the game's.</summary>
    private sealed class Character3D(NetworkObject obj, CharacterBody3D body) : PhysicsHandling(obj)
    {
        public override void PhysicsProcess()
        {
            if (!Object.IsAuthority) return;
            for (var i = 0; i < body.GetSlideCollisionCount(); i++)
                TouchCollider(body.GetSlideCollision(i).GetCollider());
        }
    }

    private sealed class Character2D(NetworkObject obj, CharacterBody2D body) : PhysicsHandling(obj)
    {
        public override void PhysicsProcess()
        {
            if (!Object.IsAuthority) return;
            for (var i = 0; i < body.GetSlideCollisionCount(); i++)
                TouchCollider(body.GetSlideCollision(i).GetCollider());
        }
    }
}
