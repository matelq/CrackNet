using CrackNet.Core.Logging;
using Godot;

namespace CrackNet.Internal;

/// <summary>
/// What the library does for a physics root so a game does not have to: freeze it where another peer simulates it,
/// pass authority on contact, and hand a settled shared body back to the host.
/// </summary>
internal abstract class PhysicsHandling
{
    private static readonly CrackNetLogger Logger = CrackNetLogger.ForCrackNet("PhysicsHandling");

    protected readonly NetworkObject Object;

    protected PhysicsHandling(NetworkObject obj) => Object = obj;

    public static PhysicsHandling? For(NetworkObject obj) => obj.Root switch
    {
        RigidBody3D body => new Rigid3D(obj, body),
        CharacterBody3D body => new Character3D(obj, body),
        _ => null,
    };

    public virtual void AuthorityChanged() { }
    public abstract void PhysicsProcess();
    public virtual void Exited() { }

    protected bool IsHost => Object.Root!.Multiplayer.IsServer();

    /// <summary>Passes authority to whatever object <paramref name="collider"/> is the root of.</summary>
    protected void TouchCollider(GodotObject? collider)
    {
        if (collider is Node node && NetworkObject.Of(node) is { } other) Object.Spread(other);
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
            // Frozen static, not kinematic: Rapier gives a kinematic body the velocity of its last move, so a copy that
            // snaps (a playback correction, a freeze, a grab) moves a metre in a frame at 60 m/s, and a character
            // standing on it keeps that as platform velocity and flies 100 m up. A static body carries no velocity
            body.FreezeMode = RigidBody3D.FreezeModeEnum.Static;
            body.ContactMonitor = true;
            body.MaxContactsReported = Math.Max(body.MaxContactsReported, 4);
            body.BodyEntered += other =>
            {
                if (Object.Authority.IsLocal && _body.LinearVelocity.Length() > TouchSpeed) TouchCollider(other);
            };
        }

        /// <summary>Every shared 3D body in play, for <see cref="UpdateGhosts"/>.</summary>
        // ponytail: every change visits every body; a spatial index when a game has hundreds of shared bodies
        private static readonly List<Rigid3D> Shared = [];
        private readonly HashSet<Rigid3D> _ghosts = [];

        public override void Exited()
        {
            Shared.Remove(this);
            foreach (var other in _ghosts) other._ghosts.Remove(this);
            _ghosts.Clear();
        }

        /// <summary>
        /// A body simulated here and a copy of one simulated elsewhere do not collide. The copy is frozen and a
        /// network delay behind: moved into bodies here, it pushed them with infinite mass and shot them off. Only the peer simulating a body resolves its hits; this one
        /// takes a copy before running into it (<see cref="TouchAhead"/>), and from then on both are simulated here.
        /// Characters still collide with copies, so a player can stand on one.
        /// </summary>
        private void UpdateGhosts()
        {
            if (Object.ResolvedKind != NetworkObject.ObjectKind.Shared) return;
            if (!Shared.Contains(this)) Shared.Add(this);
            foreach (var other in Shared)
            {
                if (other == this) continue;
                var apart = Object.Authority.IsLocal != other.Object.Authority.IsLocal;
                if (apart == _ghosts.Contains(other)) continue;
                if (apart)
                {
                    _body.AddCollisionExceptionWith(other._body);
                    _ghosts.Add(other);
                    other._ghosts.Add(this);
                }
                else
                {
                    _body.RemoveCollisionExceptionWith(other._body);
                    other._body.RemoveCollisionExceptionWith(_body);
                    _ghosts.Remove(other);
                    other._ghosts.Remove(this);
                }
            }
        }

        /// <summary>Takes the copies this body will reach within a couple of frames, before it passes into them.</summary>
        private void TouchAhead()
        {
            if (_body.LinearVelocity.Length() <= TouchSpeed) return;
            var ahead = _body.LinearVelocity * (float)(2 * _body.GetPhysicsProcessDeltaTime());
            foreach (var other in TouchingOf(_body, ahead))
                if (!other.Authority.IsLocal && other.ResolvedKind == NetworkObject.ObjectKind.Shared && other.Root is RigidBody3D)
                    Object.Spread(other);
        }

        public override void AuthorityChanged()
        {
            UpdateGhosts();
            SetFrozen(_body, !Object.Authority.IsLocal || Object.ClaimedBy != 0);
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
                Logger.Debug("{0} touches {1}: {2}", _body.Name, other.Root!.Name, Object.Spread(other));
        }

        /// <summary>The objects whose bodies touch this one: resting contacts too, which a frozen body reports none of.</summary>
        private IEnumerable<NetworkObject> Touching() => TouchingOf(_body);

        private static IEnumerable<NetworkObject> TouchingOf(RigidBody3D body, Vector3 offset = default)
        {
            if (!body.IsInsideTree()) yield break;
            // Not disposed, unlike the query below: a Godot object has one C# handle, shared with any game code that
            // holds this world, and disposing it would break theirs
            var space = body.GetWorld3D().DirectSpaceState;
            foreach (var shape in body.GetChildren().OfType<CollisionShape3D>())
            {
                if (shape.Shape is null || shape.Disabled) continue;
                // Disposed here, on the main thread: left to the .NET finalizer, its thread releases the query into the
                // physics server, and Rapier panics on any thread but the main one
                using var query = new PhysicsShapeQueryParameters3D
                {
                    Shape = shape.Shape,
                    Transform = shape.GlobalTransform.Translated(offset),
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
            if (Object.ResolvedKind != NetworkObject.ObjectKind.Shared || !Object.Authority.IsLocal || Object.ClaimedBy != 0 || IsHost)
            {
                Object.RestFrames = 0;
                if (Object.Authority.IsLocal && Object.ClaimedBy == 0) TouchAhead();
                return;
            }
            TouchAhead();
            Object.RestFrames = _body.Sleeping || _body.LinearVelocity.Length() < RestSpeed ? Object.RestFrames + 1 : 0;
            if (Object.RestFrames < RestFramesBeforeReturning) return;

            // Every body of the group has to have rested as long; a held one never counts
            var group = RestingGroup();
            if (group.Any(member => member.RestFrames < RestFramesBeforeReturning)) return;
            // Not from under a player, local or replayed: handed over, each peer would have the other body a network
            // delay behind, the two would overlap and the physics engine would throw the player up
            if (group.Any(member => member.Root is RigidBody3D body
                    && TouchingOf(body).Any(other => other.ResolvedKind == NetworkObject.ObjectKind.Personal)))
            {
                foreach (var member in group) member.RestFrames = 0;
                return;
            }
            foreach (var member in group)
            {
                member.Authority.ReturnToHost();
                member.RestFrames = 0;
            }
        }
    }

    /// <summary>A character body passes authority to what it slid into this frame; its own movement is the game's.</summary>
    private sealed class Character3D(NetworkObject obj, CharacterBody3D body) : PhysicsHandling(obj)
    {
        public override void PhysicsProcess()
        {
            if (!Object.Authority.IsLocal) return;
            NetworkObject? floor = null;
            for (var i = 0; i < body.GetSlideCollisionCount(); i++)
            {
                var collision = body.GetSlideCollision(i);   // Godot reuses these: the handle may be the game's too
                if (collision.GetCollider() is not Node node || NetworkObject.Of(node) is not { } other) continue;
                // What it stands on stays where it is: taken, a crate at rest went back to the host and was taken again
                // by the next frame's floor contact, over and over. It is the base the character rides instead: its
                // position goes out relative to that body, so a player on a moving crate or lift is drawn on it
                // everywhere rather than a playback delay behind it. The game opts out per layer with
                // platform_floor_layers, as it does for the platform's velocity
                if (collision.GetNormal().AngleTo(body.UpDirection) <= body.FloorMaxAngle)
                {
                    if (other.Root is CollisionObject3D under && (under.CollisionLayer & body.PlatformFloorLayers) != 0) floor = other;
                    continue;
                }
                // Godot's character bodies do not push rigid bodies: push the ones taken here, along the contact
                if (Object.ImpulseStrength > 0 && node is RigidBody3D) Object.Impulse(other, -collision.GetNormal() * Object.ImpulseStrength);
                else Object.Spread(other);
            }
            Object.Base = body.IsOnFloor() ? floor : null;
        }
    }
}
