using Godot;
using Godot.Collections;

namespace Netfox.Extras;

/// <summary>NetworkWeapon for 3D: projectiles are reconciled by global transform. Port of netfox.extras/weapon/network-weapon-3d.gd.</summary>
[GlobalClass]
public partial class NetworkWeapon3D : Node3D
{
    /// <summary>Maximum distance between the requested and the authoritative spawn position to accept a projectile.</summary>
    [Export] public float DistanceThreshold { get; set; } = 1.0f;

    private readonly NetworkWeaponProxy _weapon;

    public NetworkWeapon3D()
    {
        _weapon = new NetworkWeaponProxy
        {
            CanFireCallback = CanFireImpl,
            CanPeerUseCallback = CanPeerUse,
            AfterFireCallback = p => AfterFire((Node3D)p),
            SpawnCallback = Spawn,
            GetDataCallback = p => GetData((Node3D)p),
            ApplyDataCallback = (p, d) => ApplyData((Node3D)p, d),
            IsReconcilableCallback = (p, r, l) => IsReconcilable((Node3D)p, r, l),
            ReconcileCallback = (p, l, r) => Reconcile((Node3D)p, l, r),
        };
        AddChild(_weapon, true, InternalMode.Back);
        _weapon.Owner = this;
    }

    public bool CanFire() => _weapon.CanFire();
    public Node3D? Fire() => _weapon.Fire() as Node3D;
    public int GetFiredTick() => _weapon.GetFiredTick();

    protected virtual bool CanFireImpl() => false;
    protected virtual bool CanPeerUse(int peerId) => true;
    protected virtual void AfterFire(Node3D projectile) { }
    protected virtual Node3D? Spawn() => null;

    protected virtual Dictionary GetData(Node3D projectile) => new() { ["global_transform"] = projectile.GlobalTransform };

    protected virtual void ApplyData(Node3D projectile, Dictionary data) => projectile.GlobalTransform = data["global_transform"].AsTransform3D();

    protected virtual bool IsReconcilable(Node3D projectile, Dictionary requestData, Dictionary localData)
    {
        var requestPos = requestData["global_transform"].AsTransform3D().Origin;
        var localPos = localData["global_transform"].AsTransform3D().Origin;
        return requestPos.DistanceTo(localPos) < DistanceThreshold;
    }

    protected virtual void Reconcile(Node3D projectile, Dictionary localData, Dictionary remoteData)
    {
        var localTransform = localData["global_transform"].AsTransform3D();
        var remoteTransform = remoteData["global_transform"].AsTransform3D();
        var relative = projectile.GlobalTransform * localTransform.AffineInverse();
        projectile.GlobalTransform = remoteTransform * relative;
    }
}
