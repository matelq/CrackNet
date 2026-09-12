using Godot;

namespace Netfox.Examples.Playground;

/// <summary>
/// A marker that circles the arena and is only replicated to players standing near it, through the
/// <c>PeerVisibilityFilter</c> on its <c>StateSynchronizer</c>.
/// <para>
/// This is what visibility filtering is for. Without it, every peer receives every replicated property, and a client
/// that is not allowed to see something still has the data - no amount of hiding it on screen changes that. The
/// filter decides before anything is sent.
/// </para>
/// <para>
/// Filters subtract: every one of them has to agree before a peer is visible, and <c>DefaultVisibility</c> is what
/// applies when none objects. Setting it to false alongside a filter is how to make a node nobody ever receives.
/// </para>
/// <para>
/// The peers it can see are recomputed every tick loop, because this depends on where players are rather than on who
/// is in the game. The default, <c>OnPeer</c>, would decide once when someone joins and never again.
/// </para>
/// </summary>
[GlobalClass]
public partial class Beacon : Node3D
{
    [Export] public float Radius { get; set; } = 8.0f;

    /// <summary>How far a player has to be before it stops hearing about this beacon.</summary>
    [Export] public float VisibleWithin { get; set; } = 10.0f;

    [Export] public float Period { get; set; } = 12.0f;

    /// <summary>Where to look for players, to decide who is close enough.</summary>
    public Node3D PlayerRoot { get; set; } = null!;

    private Vector3 _origin;
    private StateSynchronizer _synchronizer = null!;
    private MeshInstance3D _mesh = null!;

    public override void _Ready()
    {
        _origin = Position;
        _synchronizer = GetNode<StateSynchronizer>("StateSynchronizer");
        _mesh = GetNode<MeshInstance3D>("MeshInstance3D");

        // DefaultVisibility stays true. A filter can only take visibility away - GetVisibilityFor returns false the
        // moment any filter says so, and otherwise falls back to the default - so pairing a filter with a default of
        // false makes the node visible to nobody, ever. The default is the base, the filters carve out of it.
        _synchronizer.VisibilityFilter.UpdateMode = PeerVisibilityFilter.UpdateModeEnum.PerTickLoop;
        _synchronizer.VisibilityFilter.AddVisibilityFilter(IsNear);

        NetworkTime.Instance.AfterTick += Advance;
    }

    public override void _ExitTree()
    {
        if (NetworkTime.Instance is { } time) time.AfterTick -= Advance;
    }

    /// <summary>
    /// Hides the beacon on a peer that is not being sent its position, so the filtering can be seen rather than
    /// merely trusted.
    /// <para>
    /// Without this the beacon simply stops moving on a distant client, which reads as a bug and is the opposite of
    /// the point: the client is not being lied to about where it is, it is not being told at all, and what is drawn
    /// is the last position it was ever sent. Walk towards it and it appears; walk away and it is gone.
    /// </para>
    /// </summary>
    public override void _Process(double delta)
    {
        // The host runs the beacon itself, so it always sees it. Everyone else runs the same test the host applies
        // before sending - against the stale position, which is all this peer has.
        _mesh.Visible = IsMultiplayerAuthority() || IsNear(Multiplayer.GetUniqueId());
    }

    /// <summary>Circles on the tick, so the host's copy is where it says it is regardless of frame rate.</summary>
    private void Advance(double delta, int tick)
    {
        if (!IsMultiplayerAuthority()) return;

        var phase = tick / (double)NetworkTime.Instance.Tickrate / Period * Mathf.Tau;
        Position = _origin + new Vector3(Mathf.Cos((float)phase), 0, Mathf.Sin((float)phase)) * Radius;
    }

    /// <summary>
    /// Runs for every peer, as often as the update mode says, so it stays cheap: one distance check against that
    /// peer's player.
    /// </summary>
    private bool IsNear(int peer)
    {
        var player = PlayerRoot?.GetNodeOrNull<Node3D>($"Player_{peer}");
        if (player is null) return false;
        return player.GlobalPosition.DistanceTo(GlobalPosition) <= VisibleWithin;
    }
}
