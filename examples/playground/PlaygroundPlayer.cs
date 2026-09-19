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
/// lands. <see cref="AimAt"/> is the third kind: a point, turned into a pose by the scene's <c>LookAtModifier3D</c>,
/// which moves the spine - and with it the hand the crate hangs in - in the skeleton's own modification pass.
/// </para>
/// </summary>
public partial class PlaygroundPlayer : CharacterBody3D, ISpawnedWith<int>, IImpulsed
{
    /// <summary>Metres a second at a full run: what a walk can account for when a log asks why a player moved.</summary>
    public const float Speed = 6;

    private const float JumpSpeed = 5, Gravity = 14, PushStrength = 4, ThrowSpeed = 9, ShotSpeed = 18;

    /// <summary>How far ahead the aim point sits when nobody is in front, and how near another player has to be to take it.</summary>
    private const float AimRange = 10, AimLockRange = 4;

    /// <summary>
    /// Seconds from the start of the throw's swing to the hand letting go. The clip's first half second is the arm
    /// pulling back, which the scene's slot starts past: playing it put that half second between the key and the
    /// crate, and the throw read as the item waiting for the animation to end rather than being thrown.
    /// </summary>
    private const double ThrowRelease = 0.22;

    /// <summary>
    /// The AnimationTree's one-shot slots, in the order <see cref="Gesture"/> indexes them. Each names its own clip
    /// in the scene, and the shot and the flinch are filtered there to the upper body, so the legs keep the stride
    /// they were walking. Nothing here is set on the tree at runtime: an AnimationTree's nodes are a resource shared
    /// by every player instanced from the scene, and only its parameters belong to the one player.
    /// </summary>
    private static readonly string[] OneShots = ["Throw", "PickUp", "Shoot", "Hit"];

    public int Peer { get; private set; }
    public int Slot { get; private set; }

    private bool _grabWasDown, _grabPlayerWasDown, _pushWasDown, _shootWasDown, _putDownWasDown;
    private double _throwDue = -1;
    private Node? _throwing;
    private Marker3D _hand = null!;
    private Marker3D _aimTarget = null!;
    private AnimationTree _animation = null!;

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
    /// The world point this player aims at: the upper body is turned to it by the <c>LookAtModifier3D</c> in the
    /// scene, on every peer, and a shot leaves along it. It is a point, not the player it was picked from: the
    /// authority chooses the target from what it sees, and what travels is where it decided to aim. An observer
    /// turning its copy towards where *it* draws the other player instead would aim a playback delay away from the
    /// shot that arrives, and no two screens would agree.
    /// </summary>
    [Synced] public Vector3 AimAt { get; set; }

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
            if (_gestureKnown && value.Y != field.Y && value.X >= 0 && value.X < OneShots.Length)
                _animation?.Set($"parameters/{OneShots[value.X]}/request", (int)AnimationNodeOneShot.OneShotRequest.Fire);
            _gestureKnown = true;
            field = value;
        }
    }

    /// <summary>Plays the clip in <paramref name="slot"/> once, here and on every screen this player is drawn on.</summary>
    private void Play(string slot) => Gesture = new Vector2I(Array.IndexOf(OneShots, slot), Gesture.Y + 1);

    /// <summary>A push delivered to this player - a shove, a shot, a throw - arrives on its own peer: it flinches.</summary>
    public void OnImpulsed(Vector3 impulse) => Play("Hit");

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
        _aimTarget = GetNode<Marker3D>("AimTarget");
        _animation = GetNode<AnimationTree>("AnimationTree");
        AimAt = GlobalPosition + Forward * AimRange;   // until the first sample of another player's aim lands
        _aimTarget.GlobalPosition = AimAt;
        // The clips come out of the glb as one-shots; the cycles loop. The library is shared by every knight, so this
        // is done once and holds for all
        var clips = GetNode<AnimationPlayer>("Visual/Knight/AnimationPlayer");
        foreach (var cycle in new[] { "Idle", "Running_A", "Jump_Idle" })
            clips.GetAnimation(cycle).LoopMode = Animation.LoopModeEnum.Linear;
        // This peer's own gestures start from zero; another peer's count is history until its first sample has landed
        _gestureKnown = this.Authority.IsLocal;
        if (this.Authority.IsLocal) AddToGroup("local_player");
    }

    /// <summary>
    /// Puts the modifier's target where the aim says, before the skeleton runs its modification pass: every peer
    /// poses this knight from the same number, and the item in its hand is placed after the modifier moved it.
    /// </summary>
    public override void _Process(double delta) => _aimTarget.GlobalPosition = AimAt;

    /// <summary>
    /// Where this player aims: the nearest other player within <see cref="AimLockRange"/> and roughly in front, or
    /// else a point straight ahead. Read on the authority only, from the players as this peer draws them; the result
    /// is what travels.
    /// </summary>
    private Vector3 AimPoint()
    {
        var ahead = GlobalPosition + Forward * AimRange;
        var locked = GetParent().GetChildren().OfType<PlaygroundPlayer>()
            .Where(other => other != this && other.AttachedTo != this
                            && other.GlobalPosition.DistanceTo(GlobalPosition) < AimLockRange
                            && (other.GlobalPosition - GlobalPosition).Normalized().Dot(Forward) > 0.7f)
            .MinBy(other => other.GlobalPosition.DistanceTo(GlobalPosition));
        return locked?.GlobalPosition ?? ahead;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!this.Authority.IsLocal) return;
        AimAt = AimPoint();
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
        _throwDue = Time.GetTicksMsec() / 1000.0 + ThrowRelease;
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
        // Along the aim, the same value every peer turned this knight with, so the shot leaves where the body points
        var along = (AimAt - GlobalPosition).Normalized();
        // Chest height, so a shot can hit a crate on the floor as well as another player
        var at = new Transform3D(GlobalBasis, GlobalPosition + along * 0.8f);
        PlaytestLog.Action(this, "shoot");
        Play("Shoot");
        PlaygroundShot.Spawn(at, along * ShotSpeed, parent: GetParent().GetParent<Playground>().Shots);
    }

    /// <summary>The crate this player holds lets go when the player leaves.</summary>
    public override void _ExitTree()
    {
        Playground.SetSlot(Peer, null);
        if (Held is { } held && this.Authority.IsLocal) this.Detach(held);
    }
}
