using Godot;
using Godot.Collections;
using Netfox.Extras;

namespace Netfox.Tests;

/// <summary>A projectile that only has to exist and be findable.</summary>
public partial class HarnessProjectile : Node3D
{
}

/// <summary>
/// A weapon whose every hook is a switch a case can flip: whether it can fire at all, whether a given peer may use it,
/// and where its projectiles come out.
/// </summary>
public partial class HarnessWeapon : NetworkWeapon3D
{
    public bool Ready { get; set; } = true;
    public HashSet<int> DeniedPeers { get; } = new();
    public int Spawned { get; private set; }
    public int Fired { get; private set; }
    public int Reconciled { get; private set; }
    public Node3D SpawnRoot { get; set; } = null!;

    public List<HarnessProjectile> Projectiles { get; } = new();

    protected override bool CanFireImpl() => Ready;

    protected override bool CanPeerUse(int peerId) => !DeniedPeers.Contains(peerId);

    protected override Node3D? Spawn()
    {
        Spawned++;
        var projectile = new HarnessProjectile { Name = "Projectile" };
        SpawnRoot.AddChild(projectile);
        projectile.GlobalPosition = GlobalPosition;
        Projectiles.Add(projectile);
        return projectile;
    }

    protected override void AfterFire(Node3D projectile) => Fired++;

    protected override void Reconcile(Node3D projectile, Dictionary localData, Dictionary remoteData)
    {
        Reconciled++;
        base.Reconcile(projectile, localData, remoteData);
    }

    /// <summary>Projectiles still in the tree; a declined one is freed.</summary>
    public int LiveProjectiles => Projectiles.Count(p => GodotObject.IsInstanceValid(p) && !p.IsQueuedForDeletion());
}

/// <summary>Records what its raycast found, so two peers can be compared.</summary>
public partial class HarnessHitscan : NetworkWeaponHitscan3D
{
    public int Shots { get; private set; }
    public List<ulong> Hits { get; } = new();

    protected override void OnFire() => Shots++;

    protected override void OnHit(Dictionary result) => Hits.Add(result["collider"].As<GodotObject>().GetInstanceId());
}

/// <summary>
/// #27: the weapon toolkit is fully ported but had never run. These cases put it on two stacks and drive the
/// request/accept model end to end.
/// </summary>
public partial class WeaponTests : HarnessSuite
{
    private HarnessWeapon Weapon(NetfoxStack stack, Vector3 at)
    {
        var weapon = new HarnessWeapon { Name = "Weapon", Position = at };
        weapon.SpawnRoot = weapon;
        stack.AddChild(weapon);
        return weapon;
    }

    /// <summary>Pumps frames until <paramref name="condition"/> holds; the RPCs travel over the loopback peer.</summary>
    private Task<bool> Settle(Func<bool> condition) => WaitUntil(condition, 3);

    [Test]
    public async Task AcceptedShotLeavesBothSidesWithAProjectile()
    {
        var hostWeapon = Weapon(Host, Vector3.Zero);
        var clientWeapon = Weapon(Client, Vector3.Zero);
        await NextFrame();

        var projectile = clientWeapon.Fire();
        Expect.NotNull(projectile, "the client should get its projectile straight away, before the authority answers");
        Expect.Equal(1, clientWeapon.LiveProjectiles);

        Expect.True(await Settle(() => hostWeapon.Spawned > 0 && clientWeapon.Reconciled > 0),
            $"host spawned {hostWeapon.Spawned}, client reconciled {clientWeapon.Reconciled}");

        // Accepted: the authority kept its copy, and the client kept the one it predicted
        Expect.Equal(1, hostWeapon.LiveProjectiles);
        Expect.Equal(1, clientWeapon.LiveProjectiles);
        Expect.Equal(1, hostWeapon.Fired);

        // Both copies carry the same generated id in their name and the weapon's authority, which is how a game
        // addresses the same projectile on both sides. Upstream foxssake/netfox#519 is about a projectile that keeps
        // flying on the shooter after it hit: the toolkit never despawns projectiles, so that is the game's to do.
        Expect.Equal(hostWeapon.Projectiles[0].Name.ToString(), clientWeapon.Projectiles[0].Name.ToString());
        Expect.Equal(hostWeapon.GetMultiplayerAuthority(), clientWeapon.Projectiles[0].GetMultiplayerAuthority());
        Expect.Equal(hostWeapon.GetMultiplayerAuthority(), hostWeapon.Projectiles[0].GetMultiplayerAuthority());
    }

    [Test]
    public async Task ShotBeyondTheToleranceIsDeclinedAndTheProjectileGoesAway()
    {
        // The authority's muzzle is ten metres from where the client thinks it is, well past the default one metre
        var hostWeapon = Weapon(Host, new Vector3(10, 0, 0));
        var clientWeapon = Weapon(Client, Vector3.Zero);
        await NextFrame();

        Expect.NotNull(clientWeapon.Fire());
        Expect.Equal(1, clientWeapon.LiveProjectiles);

        Expect.True(await Settle(() => clientWeapon.LiveProjectiles == 0),
            $"the declined projectile was not taken back: {clientWeapon.LiveProjectiles} still live");

        // The authority spawned one to compare against, and threw it away with the request
        Expect.Equal(1, hostWeapon.Spawned);
        Expect.Equal(0, hostWeapon.Fired);
        Expect.Equal(0, hostWeapon.LiveProjectiles);
    }

    [Test]
    public async Task PeerThatMayNotUseTheWeaponIsDeclined()
    {
        var hostWeapon = Weapon(Host, Vector3.Zero);
        var clientWeapon = Weapon(Client, Vector3.Zero);
        hostWeapon.DeniedPeers.Add(2);
        await NextFrame();

        Expect.NotNull(clientWeapon.Fire());

        Expect.True(await Settle(() => clientWeapon.LiveProjectiles == 0),
            "a peer that may not use the weapon should get its projectile taken back");
        Expect.Equal(0, hostWeapon.Spawned, "the authority should not even spawn one to compare against");
    }

    [Test]
    public async Task CooldownStopsTheShotBeforeItLeaves()
    {
        var hostWeapon = Weapon(Host, Vector3.Zero);
        var clientWeapon = Weapon(Client, Vector3.Zero);
        clientWeapon.Ready = false;
        await NextFrame();

        Expect.False(clientWeapon.CanFire());
        Expect.Null(clientWeapon.Fire(), "a weapon that cannot fire returns nothing");
        Expect.Equal(0, clientWeapon.Spawned);

        await NextFrame();
        await NextFrame();
        Expect.Equal(0, hostWeapon.Spawned, "nothing should have reached the authority");
    }

    /// <summary>The authority not being ready declines the request, rather than letting it through.</summary>
    [Test]
    public async Task AuthorityOnCooldownDeclinesTheRequest()
    {
        var hostWeapon = Weapon(Host, Vector3.Zero);
        var clientWeapon = Weapon(Client, Vector3.Zero);
        hostWeapon.Ready = false;
        await NextFrame();

        Expect.NotNull(clientWeapon.Fire());
        Expect.True(await Settle(() => clientWeapon.LiveProjectiles == 0),
            "the authority was not ready, so the projectile should have been taken back");
    }

    /// <summary>
    /// The hitscan weapon replicates nothing of its own: firing it raycasts locally. What makes it agree between peers
    /// is being fired from replicated input on each of them, which is what this case stands in for.
    /// </summary>
    [Test]
    public async Task HitscanRaycastsLocallyAndAgreesBetweenPeers()
    {
        var target = await Mount(new StaticBody3D { Name = "Target", Position = new Vector3(0, 0, -5) });
        var shape = new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(4, 4, 1) } };
        target.AddChild(shape);

        var hostGun = new HarnessHitscan { Name = "Hitscan" };
        Host.AddChild(hostGun);
        var clientGun = new HarnessHitscan { Name = "Hitscan" };
        Client.AddChild(clientGun);
        await NextFrame();
        await NextFrame();

        // Fired on the host only: nothing crosses the network, so the client stays silent
        Expect.True(hostGun.Fire());
        Expect.Equal(1, hostGun.Shots);
        await NextFrame();
        await NextFrame();
        Expect.Equal(0, clientGun.Shots, "the hitscan weapon does not replicate the shot itself");

        // Fired on both, as replicated input would: both raycast to the same body
        Expect.True(clientGun.Fire());
        Expect.Equal(1, clientGun.Shots);
        Expect.SequenceEqual(hostGun.Hits, clientGun.Hits);
        Expect.Equal(1, hostGun.Hits.Count);
        Expect.Equal(target.GetInstanceId(), hostGun.Hits[0]);
    }

    [Test]
    public async Task HitscanRespectsItsMaxDistance()
    {
        var target = await Mount(new StaticBody3D { Name = "Far Target", Position = new Vector3(0, 0, -50) });
        target.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(4, 4, 1) } });

        var gun = new HarnessHitscan { Name = "Hitscan", MaxDistance = 10 };
        Host.AddChild(gun);
        await NextFrame();
        await NextFrame();

        Expect.True(gun.Fire());
        Expect.Equal(1, gun.Shots, "the shot still counts as fired");
        Expect.Empty(gun.Hits, "nothing within ten metres, so nothing is hit");
    }
}
