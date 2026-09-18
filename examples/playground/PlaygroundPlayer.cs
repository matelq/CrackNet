using Godot;

namespace CrackNet.Examples.Playground;

/// <summary>
/// A player character. A character body, so its <see cref="NetworkObject"/> is Personal: always simulated by its own
/// peer, played back everywhere else, and it takes authority over the crates it walks into. Players do not collide
/// with each other: a push is a knock delivered to the pushed player's peer, which applies it as knockback.
/// <para>
/// It carries a crate in its <c>Hand</c> marker, which the walk animation bobs: the library puts the crate there on
/// every peer, after that peer's animation. Animation is parameters: <see cref="WalkBlend"/> drives the
/// AnimationTree's walk blend everywhere, and <see cref="Throws"/> is the one-shot pattern, a counter bumped in the
/// tick of the throw that fires the throw animation wherever the sample lands.
/// </para>
/// </summary>
public partial class PlaygroundPlayer : CharacterBody3D, ISpawnedWith<int>
{
    private const float Speed = 6, JumpSpeed = 5, Gravity = 14, PushStrength = 4, ThrowSpeed = 9, ShotSpeed = 18;

    public int Peer { get; private set; }
    public int Slot { get; private set; }

    private bool _grabWasDown, _grabPlayerWasDown, _pushWasDown, _shootWasDown;
    private Marker3D _hand = null!;
    private AnimationTree _animation = null!;
    private bool _throwsKnown;

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

    /// <summary>
    /// Throws so far. Bumped in the same tick as the crate leaves the hand, so an observer plays the throw animation in
    /// the frame it sees the crate go. The first value a late joiner receives is history, not a throw.
    /// </summary>
    [Synced]
    public int Throws
    {
        get;
        set
        {
            if (_throwsKnown && value != field) _animation?.Set("parameters/Throw/request", (int)AnimationNodeOneShot.OneShotRequest.Fire);
            _throwsKnown = true;
            field = value;
        }
    }

    /// <summary>Spawn data from the host: which colour slot this player has, the same on every peer.</summary>
    public void OnSpawned(int slot) => Slot = slot;

    public override void _EnterTree()
    {
        Peer = GetMultiplayerAuthority();
        Name = $"Player{Peer}";
        // The scene's capsule is white: this player's slot colour, and a material of its own to hold it
        var color = Playground.SlotColors[Slot % Playground.SlotColors.Length];
        GetNode<MeshInstance3D>("Visual/Body").MaterialOverride = new StandardMaterial3D { AlbedoColor = color };

        Playground.SetSlot(Peer, Slot);
    }

    public override void _Ready()
    {
        _hand = GetNode<Marker3D>("Hand");
        _animation = GetNode<AnimationTree>("AnimationTree");
        // This peer's own throws start from zero; another peer's count is history until its first sample has landed
        _throwsKnown = this.Authority.IsLocal;
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

        if (Pressed(bot?.Grab, Key.F, ref _grabWasDown)) GrabOrThrow();
        if (Pressed(null, Key.G, ref _grabPlayerWasDown)) PickUpOrThrowPlayer();
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

    private void GrabOrThrow()
    {
        if (Held is { } held)
        {
            PlaytestLog.Action(this, $"throw {held.Name}");
            // The counter and the detach in one tick: an observer sees the swing and the crate leave in the same frame
            Throws++;
            this.Detach(held);
            held.Impulse((Forward * ThrowSpeed + Vector3.Up * 2) * held.Mass);
            return;
        }

        var nearest = GetTree().GetNodesInGroup("crates").OfType<PlaygroundCrate>()
            .Where(crate => crate.GlobalPosition.DistanceTo(GlobalPosition + Forward) < 1.6f)
            .MinBy(crate => crate.GlobalPosition.DistanceTo(GlobalPosition));
        if (nearest is null) return;
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
            PlaytestLog.Action(this, $"throw {carried.Name}");
            Throws++;
            this.Detach(carried);
            carried.Impulse(Forward * ThrowSpeed + Vector3.Up * 3);
            return;
        }

        var nearest = GetParent().GetChildren().OfType<PlaygroundPlayer>()
            .Where(other => other != this && other.GlobalPosition.DistanceTo(GlobalPosition + Forward) < 1.6f)
            .MinBy(other => other.GlobalPosition.DistanceTo(GlobalPosition));
        if (nearest is null) return;
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
        PlaygroundShot.Spawn(at, Forward * ShotSpeed, parent: GetParent().GetParent<Playground>().Shots);
    }

    /// <summary>The crate this player holds lets go when the player leaves.</summary>
    public override void _ExitTree()
    {
        Playground.SetSlot(Peer, null);
        if (Held is { } held && this.Authority.IsLocal) this.Detach(held);
    }
}
