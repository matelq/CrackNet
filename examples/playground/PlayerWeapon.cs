using Godot;
using Godot.Collections;
using Netfox.Extras;

namespace Netfox.Examples.Playground;

/// <summary>
/// The player's gun, on netfox's request-and-accept model: the shooter spawns a projectile the moment it fires, and
/// the authority independently spawns its own and compares the two spawn transforms. Close enough, and the shot is
/// accepted; too far apart, and the shooter's copy is taken back.
/// <para>
/// That is what makes shooting feel immediate without trusting the client about where it shot from. It is not
/// rollback - see <see cref="Projectile"/> - and the toolkit never despawns projectiles for you.
/// </para>
/// </summary>
[GlobalClass]
public partial class PlayerWeapon : NetworkWeapon3D
{
    [Export] public PackedScene ProjectileScene { get; set; } = null!;

    /// <summary>Ticks between shots. Checked on both sides, so a client cannot fire faster by asking.</summary>
    [Export] public int CooldownTicks { get; set; } = 12;

    /// <summary>Where projectiles are parented. Set by the spawner, so they do not ride along with the player.</summary>
    public Node3D SpawnRoot { get; set; } = null!;

    /// <summary>Shots this weapon has accepted, for the sample's status line and its smoke test.</summary>
    public int Shots { get; private set; }

    private const int NeverFired = int.MinValue;

    private int _lastAcceptedTick = NeverFired;

    /// <summary>
    /// The tick the shot belongs to. On the shooter that is the current one; on the authority it is the tick the
    /// request carried, which is what keeps the two cooldown checks talking about the same moment.
    /// </summary>
    private int ShotTick => Mathf.Max(NetworkTime.Instance.Tick, GetFiredTick());

    protected override bool CanFireImpl()
        // The "never fired" case is spelled out rather than leaned on: subtracting int.MinValue overflows, and the
        // weapon then reports it can never fire, which is exactly as quiet a failure as it sounds
        => _lastAcceptedTick == NeverFired || ShotTick - _lastAcceptedTick >= CooldownTicks;

    protected override Node3D? Spawn()
    {
        if (ProjectileScene is null || SpawnRoot is null) return null;

        var projectile = ProjectileScene.Instantiate<Projectile>();
        SpawnRoot.AddChild(projectile);

        // Out of the muzzle, facing the way the weapon faces. The authority does exactly this, from its own idea of
        // where the player is - and the difference between the two is what IsReconcilable judges.
        projectile.GlobalTransform = GlobalTransform;
        return projectile;
    }

    protected override void AfterFire(Node3D projectile)
    {
        _lastAcceptedTick = ShotTick;
        Shots++;
    }
}
