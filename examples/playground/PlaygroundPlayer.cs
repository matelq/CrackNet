using Godot;

namespace Netfox.Examples.Playground;

/// <summary>
/// A player character. A character body, so its <see cref="NetworkObject"/> is Personal: always simulated by its own
/// peer, played back everywhere else, and it takes authority over the crates it walks into. Players do not collide
/// with each other: a push is a knock delivered to the pushed player's peer, which applies it as knockback.
/// </summary>
public partial class PlaygroundPlayer : CharacterBody3D, ISpawnedWith<int>
{
    private const float Speed = 6, JumpSpeed = 5, Gravity = 14, PushStrength = 4, ThrowSpeed = 9, ShotSpeed = 18;

    public int Peer { get; private set; }
    public int Slot { get; private set; }

    private PlaygroundCrate? _held;
    private bool _grabWasDown, _pushWasDown, _shootWasDown;

    private Vector3 Forward => -GlobalBasis.Z;

    /// <summary>What a player on this peer does instead of reading the keyboard; set by the headless smoke.</summary>
    public static Func<PlaygroundPlayer, (Vector3 Move, bool Grab, bool Push, bool Shoot)>? Bot { get; set; }

    /// <summary>The crate this player holds, if any.</summary>
    public PlaygroundCrate? Held => _held;

    /// <summary>Spawn data from the host: which colour slot this player has, the same on every peer.</summary>
    public void OnSpawned(int slot) => Slot = slot;

    public override void _EnterTree()
    {
        Peer = GetMultiplayerAuthority();
        Name = $"Player{Peer}";
        // The scene's capsule is white: this player's slot colour, and a material of its own to hold it
        var color = Playground.SlotColors[Slot % Playground.SlotColors.Length];
        GetNode<MeshInstance3D>("Body").MaterialOverride = new StandardMaterial3D { AlbedoColor = color };

        Playground.SetSlot(Peer, Slot);
    }

    public override void _Ready()
    {
        if (this.Authority.IsLocal) AddToGroup("local_player");
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!this.Authority.IsLocal) return;
        var dt = (float)delta;

        var bot = Bot?.Invoke(this);
        var input = bot?.Move ?? new Vector3(
            (Input.IsPhysicalKeyPressed(Key.D) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.A) ? 1 : 0), 0,
            (Input.IsPhysicalKeyPressed(Key.S) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.W) ? 1 : 0)).Normalized();
        if (bot is null && !GetWindow().HasFocus()) input = Vector3.Zero;

        var velocity = new Vector3(input.X * Speed, Velocity.Y, input.Z * Speed) + this.TakeKnockback(delta);
        velocity.Y = IsOnFloor() && GetWindow().HasFocus() && Input.IsPhysicalKeyPressed(Key.Space) ? JumpSpeed : velocity.Y - Gravity * dt;
        if (input != Vector3.Zero) Rotation = new Vector3(0, Mathf.Atan2(-input.X, -input.Z), 0);

        Velocity = velocity;
        MoveAndSlide();

        if (Pressed(bot?.Grab, Key.F, ref _grabWasDown)) GrabOrThrow();
        if (Pressed(bot?.Push, Key.E, ref _pushWasDown)) PushPlayers();
        var shootDown = bot?.Shoot ?? (GetWindow().HasFocus() && (Input.IsMouseButtonPressed(MouseButton.Left) || Input.IsPhysicalKeyPressed(Key.Enter)));
        if (shootDown && !_shootWasDown) Shoot();
        _shootWasDown = shootDown;

        if (_held is not null)
            _held.GlobalTransform = new Transform3D(GlobalBasis, GlobalPosition + Forward * 1.1f + Vector3.Up * 0.6f);
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
        if (_held is { } held)
        {
            _held = null;
            PlaytestLog.Action(this, $"throw {held.Name}");
            held.Throw(Forward * ThrowSpeed + Vector3.Up * 2);
            return;
        }

        var nearest = GetTree().GetNodesInGroup("crates").OfType<PlaygroundCrate>()
            .Where(crate => crate.GlobalPosition.DistanceTo(GlobalPosition + Forward) < 1.6f)
            .MinBy(crate => crate.GlobalPosition.DistanceTo(GlobalPosition));
        if (nearest is null) return;
        var claimed = nearest.TryClaim();
        PlaytestLog.Action(this, $"grab {nearest.Name} {(claimed ? "claimed" : "refused")}");
        if (!claimed) return;
        _held = nearest;
    }

    private void PushPlayers()
    {
        foreach (var other in GetParent().GetChildren().OfType<PlaygroundPlayer>())
        {
            if (other == this || other.GlobalPosition.DistanceTo(GlobalPosition + Forward) > 1.5f) continue;
            PlaytestLog.Action(this, $"push {other.Name}");
            other.Push((Forward + Vector3.Up * 0.1f) * PushStrength);
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
        if (_held is { } held && this.Authority.IsLocal) held.Release();
    }
}
