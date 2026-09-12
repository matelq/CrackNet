using Godot;
using Godot.Collections;

namespace Netfox.Extras;

/// <summary>Hitscan weapon: no projectile, the firing event is replicated and a raycast runs on every peer. Port of network-weapon-hitscan-3d.gd.</summary>
[GlobalClass]
public partial class NetworkWeaponHitscan3D : Node3D
{
    [Export] public float MaxDistance { get; set; } = 1000.0f;

    [Export(PropertyHint.Layers3DPhysics)] public uint CollisionMask { get; set; } = 0xFFFFFFFF;

    /// <summary>Bodies to exclude from the raycast.</summary>
    public Array<Rid> Exclude { get; set; } = new();

    private readonly NetworkWeaponProxy _weapon;

    public NetworkWeaponHitscan3D()
    {
        _weapon = new NetworkWeaponProxy
        {
            CanFireCallback = CanFireImpl,
            CanPeerUseCallback = CanPeerUse,
            AfterFireCallback = _ => AfterFire(),
            SpawnCallback = () => null,
            GetDataCallback = _ => GetData(),
            ApplyDataCallback = (_, d) => ApplyData(d),
            IsReconcilableCallback = (_, r, l) => IsReconcilable(r, l),
            ReconcileCallback = (_, l, r) => Reconcile(l, r),
        };
        AddChild(_weapon, true, InternalMode.Back);
        _weapon.Owner = this;
    }

    public bool Fire()
    {
        if (!CanFire()) return false;
        ApplyData(GetData());
        AfterFire();
        return true;
    }

    public bool CanFire() => _weapon.CanFire();

    protected virtual bool CanFireImpl() => true;
    protected virtual bool CanPeerUse(int peerId) => true;
    protected virtual void AfterFire() { }

    /// <summary>Data describing the shot: origin and forward direction.</summary>
    protected virtual Dictionary GetData() => new()
    {
        ["origin"] = GlobalTransform.Origin,
        ["direction"] = -GlobalTransform.Basis.Z,
    };

    /// <summary>Reproduces the shot: casts the ray and calls OnHit / OnFire.</summary>
    protected virtual void ApplyData(Dictionary data)
    {
        var origin = data["origin"].AsVector3();
        var direction = data["direction"].AsVector3();

        var spaceState = GetWorld3D().DirectSpaceState;
        var query = PhysicsRayQueryParameters3D.Create(origin, origin + direction * MaxDistance, CollisionMask, Exclude);
        var result = spaceState.IntersectRay(query);

        if (result.Count > 0) OnHit(result);
        OnFire();
    }

    protected virtual bool IsReconcilable(Dictionary requestData, Dictionary localData) => true;

    protected virtual void Reconcile(Dictionary localData, Dictionary remoteData) { }

    /// <summary>Raycast hit: result has position, normal, collider.</summary>
    protected virtual void OnHit(Dictionary result) { }

    protected virtual void OnFire() { }
}
