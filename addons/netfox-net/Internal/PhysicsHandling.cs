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

        public Rigid3D(NetworkObject obj, RigidBody3D body) : base(obj)
        {
            _body = body;
            body.FreezeMode = RigidBody3D.FreezeModeEnum.Kinematic;
            body.ContactMonitor = true;
            body.MaxContactsReported = Math.Max(body.MaxContactsReported, 4);
            body.BodyEntered += other =>
            {
                if (Object.Authority.IsLocal && _body.LinearVelocity.Length() > TouchSpeed) TouchCollider(other);
            };
        }

        public override void AuthorityChanged()
        {
            SetFrozen(_body, !Object.Authority.IsLocal || Object.Holder != 0);
            // Whoever takes a body takes what rests on and against it: a frozen body reports no resting contacts, and
            // left alone a stack would hang in the air here until the host's word that it fell
            if (Object.Authority.IsLocal && !IsHost) TouchOverlapping();
        }

        private void TouchOverlapping()
        {
            var touching = Touching().ToList();
            Logger.Debug("{0} taken at {1}: overlap query found {2}", _body.Name, _body.GlobalPosition,
                touching.Count == 0 ? "nothing" : string.Join(", ", touching.Select(other => other.Root!.Name)));
            foreach (var other in touching)
                Logger.Debug("{0} touches {1}: {2}", _body.Name, other.Root!.Name, Object.Touch(other));
        }

        /// <summary>The objects whose bodies touch this one: resting contacts too, which a frozen body reports none of.</summary>
        private IEnumerable<NetworkObject> Touching() => TouchingOf(_body);

        private static IEnumerable<NetworkObject> TouchingOf(RigidBody3D body)
        {
            if (!body.IsInsideTree()) yield break;
            var space = body.GetWorld3D().DirectSpaceState;
            foreach (var shape in body.GetChildren().OfType<CollisionShape3D>())
            {
                if (shape.Shape is null || shape.Disabled) continue;
                var query = new PhysicsShapeQueryParameters3D
                {
                    Shape = shape.Shape,
                    Transform = shape.GlobalTransform,
                    Margin = 0.05f,
                    CollisionMask = body.CollisionMask,
                    Exclude = [body.GetRid()],
                };
                foreach (var hit in space.IntersectShape(query, 16))
                    if (hit["collider"].AsGodotObject() is Node node && NetworkObject.Of(node) is { } other)
                        yield return other;
            }
        }

        /// <summary>
        /// The bodies this peer simulates that touch this one, and the ones touching those: a stack or a pile. It goes
        /// back to the host as a whole or not at all. Handed back one crate at a time, a pile ends up simulated half here
        /// and half on the host; the host's half bumps this peer's and takes it, and what was taken hangs on this
        /// peer's screen for a network delay.
        /// </summary>
        private List<NetworkObject> RestingGroup()
        {
            var group = new List<NetworkObject> { Object };
            for (var i = 0; i < group.Count; i++)
            {
                if (group[i].Root is not RigidBody3D body) continue;
                foreach (var other in TouchingOf(body))
                    // Only shared bodies: a player leaning on a crate keeps its own authority and has no rest to wait for
                    if (other.IsAuthority && other.Root is RigidBody3D && other.ResolvedKind == NetworkObject.ObjectKind.Shared
                        && !group.Contains(other))
                        group.Add(other);
            }
            return group;
        }

        public override void PhysicsProcess()
        {
            if (Object.ResolvedKind != NetworkObject.ObjectKind.Shared || !Object.Authority.IsLocal || Object.Holder != 0 || IsHost)
            {
                Object.RestFrames = 0;
                return;
            }
            Object.RestFrames = _body.Sleeping || _body.LinearVelocity.Length() < RestSpeed ? Object.RestFrames + 1 : 0;
            if (Object.RestFrames < RestFramesBeforeReturning) return;

            // Every body of the group has to have rested as long; a held one never counts
            var group = RestingGroup();
            if (group.Any(member => member.RestFrames < RestFramesBeforeReturning)) return;
            foreach (var member in group)
            {
                member.Authority.ReturnToHost();
                member.RestFrames = 0;
            }
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
                if (Object.Authority.IsLocal && _body.LinearVelocity.Length() > TouchSpeed) TouchCollider(other);
            };
        }

        public override void AuthorityChanged()
        {
            var frozen = !Object.Authority.IsLocal || Object.Holder != 0;
            if (_body.Freeze != frozen)
            {
                var transform = _body.GlobalTransform;
                _body.Freeze = frozen;
                _body.GlobalTransform = transform;
                PhysicsServer2D.BodySetState(_body.GetRid(), PhysicsServer2D.BodyState.Transform, transform);
            }
            if (!Object.Authority.IsLocal || IsHost || !_body.IsInsideTree()) return;

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
            if (Object.ResolvedKind != NetworkObject.ObjectKind.Shared || !Object.Authority.IsLocal || Object.Holder != 0 || IsHost)
            {
                _restFrames = 0;
                return;
            }
            _restFrames = _body.Sleeping || _body.LinearVelocity.Length() < RestSpeed ? _restFrames + 1 : 0;
            if (_restFrames >= RestFramesBeforeReturning && Object.Authority.ReturnToHost()) _restFrames = 0;
        }
    }

    /// <summary>A character body passes authority to what it slid into this frame; its own movement is the game's.</summary>
    private sealed class Character3D(NetworkObject obj, CharacterBody3D body) : PhysicsHandling(obj)
    {
        public override void PhysicsProcess()
        {
            if (!Object.Authority.IsLocal) return;
            for (var i = 0; i < body.GetSlideCollisionCount(); i++)
            {
                var collision = body.GetSlideCollision(i);
                if (collision.GetCollider() is not Node node || NetworkObject.Of(node) is not { } other) continue;
                // What it stands on stays where it is: taken, a crate at rest went back to the host and was taken again
                // by the next frame's floor contact, over and over
                if (collision.GetNormal().AngleTo(body.UpDirection) <= body.FloorMaxAngle) continue;
                // Godot's character bodies do not push rigid bodies: push the ones taken here, along the contact
                if (Object.PushStrength > 0 && node is RigidBody3D) Object.Push(other, -collision.GetNormal() * Object.PushStrength);
                else Object.Touch(other);
            }
        }
    }

    private sealed class Character2D(NetworkObject obj, CharacterBody2D body) : PhysicsHandling(obj)
    {
        public override void PhysicsProcess()
        {
            if (!Object.Authority.IsLocal) return;
            for (var i = 0; i < body.GetSlideCollisionCount(); i++)
            {
                var collision = body.GetSlideCollision(i);
                if (collision.GetCollider() is not Node node || NetworkObject.Of(node) is not { } other) continue;
                var normal = collision.GetNormal();
                if (normal.AngleTo(body.UpDirection) <= body.FloorMaxAngle) continue;   // what it stands on, as in 3D
                if (Object.PushStrength > 0 && node is RigidBody2D) Object.Push(other, new Vector3(-normal.X, -normal.Y, 0) * Object.PushStrength);
                else Object.Touch(other);
            }
        }
    }
}
