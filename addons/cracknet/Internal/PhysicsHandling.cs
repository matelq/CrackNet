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

        /// <summary>
        /// At rest against what it touches: on the floor slower than <see cref="RestSpeed"/>, on a moving platform
        /// slower than that relative to the platform, with a margin that grows with the platform's speed. A copy of a
        /// platform is moved by playback in render frames, so the velocity the engine reports for it, and the
        /// bouncing of a body riding it, are noisy in proportion to its speed; a crate riding a lift on a guest is
        /// better off back with the host, on the real lift, than judged never to rest.
        /// </summary>
        private bool RestsOnWhatItTouches()
        {
            if (_body.LinearVelocity.Length() < RestSpeed) return true;
            // On a replicated body, judged by how the offset from it moves, averaged: a copy of a platform is moved by
            // playback in render frames, so the velocity the engine reports for it swings by half (3.1-4.9 m/s for 4)
            if (Object.Base is not null && _relativeVelocity is { } relative)
                return relative.Length() < RestSpeed + 0.3f * _baseVelocity.Length();
            foreach (var other in _body.GetCollidingBodies())
            {
                if (other is not PhysicsBody3D under) continue;
                var underVelocity = PhysicsServer3D.BodyGetState(under.GetRid(), PhysicsServer3D.BodyState.LinearVelocity).AsVector3();
                if (underVelocity.Length() < RestSpeed) continue;
                if ((_body.LinearVelocity - underVelocity).Length() < RestSpeed + 0.3f * underVelocity.Length()) return true;
            }
            return false;
        }

        /// <summary>
        /// The replicated body this one rests on, from the frame's contacts: its position goes out relative to that
        /// body, as a character's does, so a crate a guest drops on the host's moving platform is drawn on the host's
        /// platform where the guest has it on its copy, not a network delay's travel behind. A sleeping body reports
        /// no contacts: the base stays until it wakes.
        /// </summary>
        private void UpdateBase()
        {
            if (!Object.Authority.IsLocal)
            {
                Object.Base = null;
                return;
            }
            var previous = Object.Base;
            if (_body.Sleeping)
            {
                // Asleep it reports no contacts, and the engine does not wake it for a body that merely moves away
                // from under it: the base stays only while it is still there. Crates from the third playtest had
                // fallen asleep against a player, kept the player as their base, and walked off with it on every
                // other screen
                if (previous is { Root: RigidBody3D } && !Touching().Any(other => ReferenceEquals(other, previous)))
                {
                    Object.Base = null;
                    TrackBase(null);
                }
                return;
            }
            NetworkObject? under = null;
            var state = PhysicsServer3D.BodyGetDirectState(_body.GetRid());
            for (var i = 0; state is not null && i < state.GetContactCount(); i++)
            {
                if (state.GetContactLocalNormal(i).Dot(Vector3.Up) < 0.7f) continue;
                // Never a player: a crate on a player's head is the player's to carry, and a player's copy moves by
                // playback, which the engine does not report as a moving floor
                if (state.GetContactColliderObject(i) is Node node && NetworkObject.Of(node) is { } other && !ReferenceEquals(other, Object)
                    && other.ResolvedKind != NetworkObject.ObjectKind.Personal)
                {
                    under = other;
                    break;
                }
            }
            Object.Base = under;
            TrackBase(ReferenceEquals(under, previous) ? under : null);
        }

        private Vector3? _lastRelative;
        private Vector3? _lastBasePosition;
        private Vector3? _relativeVelocity;
        private Vector3 _baseVelocity;

        /// <summary>The body's velocity relative to its base and the base's own, from positions, averaged over about 80 ms.</summary>
        private void TrackBase(NetworkObject? sameBase)
        {
            if (sameBase is not { Root: Node3D floor })
            {
                _lastRelative = _lastBasePosition = _relativeVelocity = null;
                return;
            }
            var dt = (float)_body.GetPhysicsProcessDeltaTime();
            var relative = floor.ToLocal(_body.GlobalPosition);
            if (_lastRelative is { } lastRelative && _lastBasePosition is { } lastBase)
            {
                _relativeVelocity = (_relativeVelocity ?? Vector3.Zero).Lerp((relative - lastRelative) / dt, 0.2f);
                _baseVelocity = _baseVelocity.Lerp((floor.GlobalPosition - lastBase) / dt, 0.2f);
            }
            _lastRelative = relative;
            _lastBasePosition = floor.GlobalPosition;
        }

        /// <summary>Whether <paramref name="member"/> touches a rigid body this peer holds (attached, collisions off).</summary>
        private static bool RestsOnAHeldItem(NetworkObject member)
        {
            foreach (var held in Shared)
            {
                if (held.Object.AttachedTo is null || !held.Object.Authority.IsLocal || ReferenceEquals(held.Object, member)) continue;
                if (TouchingOf(held._body, mask: uint.MaxValue).Any(other => ReferenceEquals(other, member))) return true;
            }
            return false;
        }

        /// <summary>The objects whose bodies touch this one: resting contacts too, which a frozen body reports none of.</summary>
        private IEnumerable<NetworkObject> Touching() => TouchingOf(_body);

        private static IEnumerable<NetworkObject> TouchingOf(RigidBody3D body, Vector3 offset = default, uint? mask = null)
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
                    CollisionMask = mask ?? body.CollisionMask,
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
            UpdateBase();
            if (Object.ResolvedKind != NetworkObject.ObjectKind.Shared || !Object.Authority.IsLocal || Object.ClaimedBy != 0 || IsHost)
            {
                Object.RestFrames = 0;
                if (Object.Authority.IsLocal && Object.ClaimedBy == 0) TouchAhead();
                return;
            }
            TouchAhead();
            Object.RestFrames = _body.Sleeping || RestsOnWhatItTouches() ? Object.RestFrames + 1 : 0;
            if (Object.RestFrames < RestFramesBeforeReturning) return;

            // Every body of the group has to have rested as long; a held one never counts
            var group = RestingGroup();
            if (group.Any(member => member.RestFrames < RestFramesBeforeReturning)) return;
            // Not from under a player, local or replayed: handed over, each peer would have the other body a network
            // delay behind, the two would overlap and the physics engine would throw the player up. Nor from on top
            // of a held item: handed over, it would hang in the air here until the host's word that it fell once the
            // item is lifted away. A held item's collisions are off, so no query from the group finds it; the query
            // goes from the item instead, over every layer
            if (group.Any(member => member.Root is RigidBody3D body
                    && TouchingOf(body).Any(other => other.ResolvedKind == NetworkObject.ObjectKind.Personal))
                || group.Any(RestsOnAHeldItem))
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
            var floorSeen = false;
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
                    floorSeen = true;
                    if (other.Root is CollisionObject3D under && (under.CollisionLayer & body.PlatformFloorLayers) != 0) floor = other;
                    continue;
                }
                // Godot's character bodies do not push rigid bodies: push the ones taken here, along the contact
                if (Object.ImpulseStrength > 0 && node is RigidBody3D) Object.Impulse(other, -collision.GetNormal() * Object.ImpulseStrength);
                else Object.Spread(other);
            }
            // Carried up by a rising platform, or snapped to the floor, the engine reports on-floor without a slide
            // collision at all: the base then stays what it was, if it is still under the feet. So does a jump: in
            // the air the player is still on the lift in every sense that matters to the screens that show it against
            // the lift, and a world position meanwhile put it into a lift shown at another moment. A floor of another
            // kind (the ground) ends it, seen as a collision or, standing still, by a ray under the feet: a player who
            // had stepped off a platform onto the ground rode along with it on every other screen
            if (!body.IsOnFloor()) return;
            var standing = floorSeen || Object.Base is null ? floor ?? (FloorOfAnotherKind(body) ? null : Object.Base) : Object.Base;
            // The base is what is under the feet, whatever the contacts said: a platform brushing past a player on the
            // ground touches it at an edge, with a normal that passes for a floor
            if (standing is { Root: CollisionObject3D beneath } && !StandsOn(body, beneath)) standing = null;
            Object.Base = standing;
        }

        /// <summary>Whether a ray from the character's origin down through its feet hits <paramref name="under"/> first.</summary>
        private static bool StandsOn(CharacterBody3D body, CollisionObject3D under)
        {
            // Pressed a little further down, does its own shape still meet what it stands on? A ray from the middle
            // of the character instead would miss a platform it stands on the very edge of, find the ground far
            // below, and take the platform away: the copy is then drawn from world positions while the platform
            // goes on without it, which is a rider sliding off the edge on every screen but its own
            var collision = new KinematicCollision3D();
            if (body.TestMove(body.GlobalTransform, Vector3.Down * FootProbe, collision, maxCollisions: 4))
            {
                for (var i = 0; i < collision.GetCollisionCount(); i++)
                    if (collision.GetCollider(i) == under) return true;
                return false;
            }
            // Margins leave a body at rest meeting nothing in so short a press. Then ask what is under the middle of
            // it, which is the whole answer for a character standing in the middle of anything
            using var query = PhysicsRayQueryParameters3D.Create(body.GlobalPosition, body.GlobalPosition + Vector3.Down * 3, body.CollisionMask, [body.GetRid()]);
            var below = body.GetWorld3D().DirectSpaceState.IntersectRay(query);
            return below.Count == 0 || below["collider"].AsGodotObject() == under;
        }

        /// <summary>How far down a character is pressed to ask what it is standing on.</summary>
        private const float FootProbe = 0.1f;

        /// <summary>Whether a floor contact this frame was with something that is not a replicated object.</summary>
        private static bool FloorOfAnotherKind(CharacterBody3D body)
        {
            for (var i = 0; i < body.GetSlideCollisionCount(); i++)
            {
                var collision = body.GetSlideCollision(i);
                if (collision.GetNormal().AngleTo(body.UpDirection) <= body.FloorMaxAngle
                    && (collision.GetCollider() is not Node node || NetworkObject.Of(node) is null))
                    return true;
            }
            return false;
        }
    }
}
