using Godot;

namespace CrackNet.Examples.Playground;

/// <summary>
/// A player character. A character body, so its <see cref="NetworkObject"/> is Personal: always simulated by its own
/// peer, played back everywhere else, and it takes authority over the crates it walks into. Players do not collide
/// with each other: a push is a knock delivered to the pushed player's peer, which applies it as knockback.
/// <para>
/// The model is KayKit's knight (CC0, examples/playground/assets/kaykit), rigged and animated. It carries a crate in
/// <see cref="Hand"/>, a marker under the bone attachment of its right hand slot: the library puts the crate there on
/// every peer, after that peer's animation and the skeleton's deferred update. Animation is parameters:
/// <see cref="WalkBlend"/> drives the AnimationTree's idle-to-run blend everywhere, and <see cref="Gesture"/> is the
/// one-shot pattern: a clip and a counter bumped in the tick of the action, which fire the clip wherever the sample
/// lands.
/// </para>
/// </summary>
public partial class PlaygroundPlayer : CharacterBody3D, ISpawnedWith<int>, IImpulsed
{
    private const float Speed = 6, JumpSpeed = 5, Gravity = 14, PushStrength = 4, ThrowSpeed = 9, ShotSpeed = 18;

    /// <summary>
    /// Seconds into the Throw clip at which the hand is fastest forward and lets go. The clip's first half second is
    /// the wind-up, which <see cref="OneShots"/> starts past: waiting it out put half a second between the key and
    /// the crate, and the throw read as the item waiting for the animation to finish rather than leaving the hand.
    /// </summary>
    private const double ThrowRelease = 0.72;

    /// <summary>
    /// The clips the one-shot slots can play, in the order <see cref="Gesture"/> indexes them: where in the clip to
    /// start, and whether it plays on the upper body alone. A shot or a flinch leaves the legs to the walk, so they
    /// do not stop mid-stride for a second; a throw or a pick-up is the whole body.
    /// </summary>
    private static readonly (string Clip, float Start, bool UpperBody)[] OneShots =
    [
        ("Throw", (float)ThrowRelease - 0.22f, false),
        ("PickUp", 0, false),
        ("1H_Ranged_Shoot", 0, true),
        ("Hit_A", 0, true),
    ];

    /// <summary>The bones an upper-body one-shot is allowed to move; the rest stay with whatever the legs are doing.</summary>
    private static readonly string[] UpperBody =
    [
        "spine", "chest", "head", "upperarm.l", "lowerarm.l", "wrist.l", "hand.l", "handslot.l",
        "upperarm.r", "lowerarm.r", "wrist.r", "hand.r", "handslot.r",
        "elbowIK.l", "handIK.l", "elbowIK.r", "handIK.r",
    ];

    public int Peer { get; private set; }
    public int Slot { get; private set; }

    private bool _grabWasDown, _grabPlayerWasDown, _pushWasDown, _shootWasDown, _putDownWasDown;
    private double _throwDue = -1;
    private Node? _throwing;
    private Marker3D _hand = null!;
    private AnimationTree _animation = null!;
    private AnimationNodeAnimation? _gestureClip, _upperClip;
    private AnimationPlayer _clips = null!;

    /// <summary>Where a carried crate goes: the right hand, moved by the skeleton.</summary>
    public Marker3D Hand => _hand;
    private bool _gestureKnown;

    private Vector3 Forward => -GlobalBasis.Z;

    /// <summary>What a player on this peer does instead of reading the keyboard; set by the headless smoke.</summary>
    public static Func<PlaygroundPlayer, (Vector3 Move, bool Grab, bool Push, bool Shoot)>? Bot { get; set; }

    /// <summary>The crate this player carries, as this peer shows it: state read, never remembered from a call.</summary>
    public PlaygroundCrate? Held => this.Attached.OfType<PlaygroundCrate>().FirstOrDefault();

    /// <summary>How much of the walk animation plays, 0 standing to 1 at full speed: an AnimationTree parameter, synced like any state.</summary>
    [Synced]
    public float WalkBlend
    {
        get;
        set
        {
            field = value;
            _animation?.Set("parameters/Walk/blend_amount", value);
        }
    }

    /// <summary>Whether this player is in the air: the AnimationTree's air blend, synced like the walk.</summary>
    [Synced]
    public float AirBlend
    {
        get;
        set
        {
            field = value;
            _animation?.Set("parameters/Air/blend_amount", value);
        }
    }

    /// <summary>
    /// The one-shot slot: which of <see cref="OneShots"/> played last, and how many have played. The clip and the
    /// counter are one value so that one sample carries both; a counter of its own would fire before the clip beside
    /// it had been applied. Bumped in the tick the action starts - a throw's item leaves the hand
    /// <see cref="ThrowRelease"/> later, when the clip lets go, so an observer plays the swing and sees the item go on
    /// the right frame of it. The first value a late joiner receives is history, played zero times.
    /// </summary>
    [Synced]
    public Vector2I Gesture
    {
        get;
        set
        {
            if (_gestureKnown && value.Y != field.Y && value.X >= 0 && value.X < OneShots.Length && _gestureClip is not null)
            {
                var (clip, start, upperBody) = OneShots[value.X];
                var slot = upperBody ? _upperClip! : _gestureClip;
                slot.Animation = clip;
                // A start offset is only read off a custom timeline, and the timeline is what is left of the clip:
                // stretching it would play the swing slower the more of the wind-up is cut
                slot.UseCustomTimeline = start > 0;
                slot.StretchTimeScale = false;
                slot.StartOffset = start;
                slot.TimelineLength = Mathf.Max(0.01f, _clips.GetAnimation(clip).Length - start);
                _animation.Set(upperBody ? "parameters/Upper/request" : "parameters/Gesture/request", (int)AnimationNodeOneShot.OneShotRequest.Fire);
            }
            _gestureKnown = true;
            field = value;
        }
    }

    /// <summary>Plays <paramref name="clip"/> once, here and on every screen this player is drawn on.</summary>
    private void Play(string clip) => Gesture = new Vector2I(Array.FindIndex(OneShots, one => one.Clip == clip), Gesture.Y + 1);

    /// <summary>A push delivered to this player - a shove, a shot, a throw - arrives on its own peer: it flinches.</summary>
    public void OnImpulsed(Vector3 impulse) => Play("Hit_A");

    /// <summary>Spawn data from the host: which colour slot this player has, the same on every peer.</summary>
    public void OnSpawned(int slot) => Slot = slot;

    public override void _EnterTree()
    {
        Peer = GetMultiplayerAuthority();
        Name = $"Player{Peer}";
        // The knight comes with one texture for every player: this player's slot colour over it, in materials of
        // its own; the weapons and shields in its hand slots stay put away
        var color = Playground.SlotColors[Slot % Playground.SlotColors.Length];
        var knight = GetNode<Node3D>("Visual/Knight");
        foreach (var slot in new[] { "handslot_l", "handslot_r" })
            foreach (var gear in knight.GetNode("Rig/Skeleton3D/" + slot).GetChildren().OfType<MeshInstance3D>())
                gear.Visible = false;
        foreach (var mesh in knight.FindChildren("*", "MeshInstance3D", recursive: true).OfType<MeshInstance3D>())
        {
            if (!mesh.Visible || mesh.GetActiveMaterial(0) is not StandardMaterial3D material) continue;
            var tinted = (StandardMaterial3D)material.Duplicate();
            tinted.AlbedoColor = color.Lerp(Colors.White, 0.4f);
            mesh.MaterialOverride = tinted;
        }

        Playground.SetSlot(Peer, Slot);
    }

    public override void _Ready()
    {
        _hand = GetNode<Marker3D>("Visual/Knight/Rig/Skeleton3D/handslot_r/Hand");
        _animation = GetNode<AnimationTree>("AnimationTree");
        // Every player instanced from the scene shares its blend tree: a copy of its own lets this one point the
        // slot at its own clip, as the tint above gives it materials of its own
        _animation.TreeRoot = (AnimationRootNode)_animation.TreeRoot.Duplicate(true);
        var tree = (AnimationNodeBlendTree)_animation.TreeRoot;
        _gestureClip = tree.GetNode("GestureAnimation") as AnimationNodeAnimation;
        _upperClip = tree.GetNode("UpperAnimation") as AnimationNodeAnimation;
        // The upper slot is told which bones it may move; everything left out keeps what the walk below it does
        var upper = (AnimationNodeOneShot)tree.GetNode("Upper");
        upper.FilterEnabled = true;
        foreach (var bone in UpperBody) upper.SetFilterPath($"Rig/Skeleton3D:{bone}", true);
        // The clips come out of the glb as one-shots; the cycles loop. The library is shared by every knight, so this
        // is done once and holds for all
        _clips = GetNode<AnimationPlayer>("Visual/Knight/AnimationPlayer");
        foreach (var cycle in new[] { "Idle", "Running_A", "Jump_Idle" })
            _clips.GetAnimation(cycle).LoopMode = Animation.LoopModeEnum.Linear;
        // This peer's own gestures start from zero; another peer's count is history until its first sample has landed
        _gestureKnown = this.Authority.IsLocal;
        if (this.Authority.IsLocal) AddToGroup("local_player");
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!this.Authority.IsLocal) return;
        // Carried by another player: the library places this body, so no movement of its own; G wriggles free
        if (this.AttachedTo is PlaygroundPlayer carrier)
        {
            WalkBlend = 0;
            if (Pressed(null, Key.G, ref _grabPlayerWasDown))
            {
                PlaytestLog.Action(this, $"wriggle free of {carrier.Name}");
                carrier.Detach(this);
            }
            return;
        }
        var dt = (float)delta;

        var bot = Bot?.Invoke(this);
        var input = bot?.Move ?? new Vector3(
            (Input.IsPhysicalKeyPressed(Key.D) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.A) ? 1 : 0), 0,
            (Input.IsPhysicalKeyPressed(Key.S) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.W) ? 1 : 0)).Normalized();
        if (bot is null && !GetWindow().HasFocus()) input = Vector3.Zero;

        var velocity = new Vector3(input.X * Speed, Velocity.Y, input.Z * Speed) + this.ImpulseVelocity;
        velocity.Y = IsOnFloor() && GetWindow().HasFocus() && Input.IsPhysicalKeyPressed(Key.Space) ? JumpSpeed : velocity.Y - Gravity * dt;
        if (input != Vector3.Zero) Rotation = new Vector3(0, Mathf.Atan2(-input.X, -input.Z), 0);

        Velocity = velocity;
        MoveAndSlide();
        WalkBlend = Mathf.Clamp(new Vector2(Velocity.X, Velocity.Z).Length() / Speed, 0, 1);
        AirBlend = IsOnFloor() ? 0 : 1;

        if (_throwDue >= 0 && Time.GetTicksMsec() / 1000.0 >= _throwDue) Release();
        if (Pressed(bot?.Grab, Key.F, ref _grabWasDown)) GrabOrThrow();
        if (Pressed(null, Key.G, ref _grabPlayerWasDown)) PickUpOrThrowPlayer();
        if (Pressed(null, Key.Q, ref _putDownWasDown)) PutDown();
        if (Pressed(bot?.Push, Key.E, ref _pushWasDown)) PushPlayers();
        var shootDown = bot?.Shoot ?? (GetWindow().HasFocus() && (Input.IsMouseButtonPressed(MouseButton.Left) || Input.IsPhysicalKeyPressed(Key.Enter)));
        if (shootDown && !_shootWasDown) Shoot();
        _shootWasDown = shootDown;
    }

    private bool Pressed(bool? botDown, Key key, ref bool wasDown)
    {
        var down = botDown ?? (GetWindow().HasFocus() && Input.IsPhysicalKeyPressed(key));
        var pressed = down && !wasDown;
        wasDown = down;
        return pressed;
    }

    /// <summary>Starts the swing; <see cref="Release"/> lets go when the clip does.</summary>
    private void Throw(Node item)
    {
        if (_throwDue >= 0) return;   // one swing at a time
        PlaytestLog.Action(this, $"throw {item.Name}");
        Play("Throw");
        _throwing = item;
        _throwDue = Time.GetTicksMsec() / 1000.0 + ThrowRelease - OneShots[0].Start;
    }

    private void Release()
    {
        _throwDue = -1;
        var item = _throwing;
        _throwing = null;
        if (item is null || !this.Attached.Contains(item)) return;   // put down or taken meanwhile
        this.Detach(item);
        switch (item)
        {
            case PlaygroundCrate crate: crate.Impulse((Forward * ThrowSpeed + Vector3.Up * 2) * crate.Mass); break;
            case PlaygroundPlayer player: player.Impulse(Forward * ThrowSpeed + Vector3.Up * 3); break;
        }
    }

    /// <summary>Puts down whatever is carried, crate or player, without a throw.</summary>
    private void PutDown()
    {
        var carried = this.Attached.ToArray();
        if (carried.Length == 0) return;
        Play("PickUp");
        foreach (var item in carried)
        {
            PlaytestLog.Action(this, $"put down {item.Name}");
            this.Detach(item);
        }
    }

    private void GrabOrThrow()
    {
        if (Held is { } held)
        {
            Throw(held);
            return;
        }

        var nearest = GetTree().GetNodesInGroup("crates").OfType<PlaygroundCrate>()
            .Where(crate => crate.GlobalPosition.DistanceTo(GlobalPosition + Forward) < 1.6f)
            .MinBy(crate => crate.GlobalPosition.DistanceTo(GlobalPosition));
        if (nearest is null) return;
        Play("PickUp");
        var attached = this.TryAttach(nearest, _hand);
        PlaytestLog.Action(this, $"grab {nearest.Name} {(attached ? "attached" : "refused")}");
    }

    /// <summary>
    /// Picks up the player in front, or throws the one carried. The same call as for a crate: the player keeps its
    /// authority, the host arbitrates who got there first, and its own peer hangs it from the record.
    /// </summary>
    private void PickUpOrThrowPlayer()
    {
        if (this.Attached.OfType<PlaygroundPlayer>().FirstOrDefault() is { } carried)
        {
            Throw(carried);
            return;
        }

        var nearest = GetParent().GetChildren().OfType<PlaygroundPlayer>()
            .Where(other => other != this && other.GlobalPosition.DistanceTo(GlobalPosition + Forward) < 1.6f)
            .MinBy(other => other.GlobalPosition.DistanceTo(GlobalPosition));
        if (nearest is null) return;
        Play("PickUp");
        var asked = this.TryAttach(nearest, _hand);
        PlaytestLog.Action(this, $"pick up {nearest.Name} {(asked ? "asked" : "refused")}");
    }

    private void PushPlayers()
    {
        foreach (var other in GetParent().GetChildren().OfType<PlaygroundPlayer>())
        {
            if (other == this || other.GlobalPosition.DistanceTo(GlobalPosition + Forward) > 1.5f) continue;
            PlaytestLog.Action(this, $"push {other.Name}");
            other.Impulse((Forward + Vector3.Up * 0.1f) * PushStrength);
        }
    }

    private void Shoot()
    {
        // Chest height, so a shot can hit a crate on the floor as well as another player
        var at = new Transform3D(GlobalBasis, GlobalPosition + Forward * 0.8f);
        PlaytestLog.Action(this, "shoot");
        Play("1H_Ranged_Shoot");
        PlaygroundShot.Spawn(at, Forward * ShotSpeed, parent: GetParent().GetParent<Playground>().Shots);
    }

    /// <summary>The crate this player holds lets go when the player leaves.</summary>
    public override void _ExitTree()
    {
        Playground.SetSlot(Peer, null);
        if (Held is { } held && this.Authority.IsLocal) this.Detach(held);
    }
}
