using Godot;
using Godot.Collections;

namespace Netfox.Extras;

/// <summary>NetworkWeapon for 2D: projectiles are reconciled by global transform. Port of netfox.extras/weapon/network-weapon-2d.gd.</summary>
[GlobalClass]
public partial class NetworkWeapon2D : Node2D
{
    /// <summary>Maximum distance between the requested and the authoritative spawn position to accept a projectile.</summary>
    [Export] public float DistanceThreshold { get; set; } = 1.0f;

    private readonly NetworkWeaponProxy _weapon;

    public NetworkWeapon2D()
    {
        _weapon = new NetworkWeaponProxy
        {
            CanFireCallback = CanFireImpl,
            CanPeerUseCallback = CanPeerUse,
            AfterFireCallback = p => AfterFire((Node2D)p),
            SpawnCallback = Spawn,
            GetDataCallback = p => GetData((Node2D)p),
            ApplyDataCallback = (p, d) => ApplyData((Node2D)p, d),
            IsReconcilableCallback = (p, r, l) => IsReconcilable((Node2D)p, r, l),
            ReconcileCallback = (p, l, r) => Reconcile((Node2D)p, l, r),
        };
        AddChild(_weapon, true, InternalMode.Back);
        _weapon.Owner = this;
    }

    public bool CanFire() => _weapon.CanFire();
    public Node2D? Fire() => _weapon.Fire() as Node2D;
    public int GetFiredTick() => _weapon.GetFiredTick();

    protected virtual bool CanFireImpl() => false;
    protected virtual bool CanPeerUse(int peerId) => true;
    protected virtual void AfterFire(Node2D projectile) { }
    protected virtual Node2D? Spawn() => null;

    protected virtual Dictionary GetData(Node2D projectile) => new() { ["global_transform"] = projectile.GlobalTransform };

    protected virtual void ApplyData(Node2D projectile, Dictionary data) => projectile.GlobalTransform = data["global_transform"].AsTransform2D();

    protected virtual bool IsReconcilable(Node2D projectile, Dictionary requestData, Dictionary localData)
    {
        var requestPos = requestData["global_transform"].AsTransform2D().Origin;
        var localPos = localData["global_transform"].AsTransform2D().Origin;
        return requestPos.DistanceTo(localPos) < DistanceThreshold;
    }

    protected virtual void Reconcile(Node2D projectile, Dictionary localData, Dictionary remoteData)
    {
        var localTransform = localData["global_transform"].AsTransform2D();
        var remoteTransform = remoteData["global_transform"].AsTransform2D();
        var relative = projectile.GlobalTransform * localTransform.AffineInverse();
        projectile.GlobalTransform = remoteTransform * relative;
    }
}
