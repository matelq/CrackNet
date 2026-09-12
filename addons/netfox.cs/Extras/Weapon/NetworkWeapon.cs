using Godot;
using Godot.Collections;
using Netfox.Core.Logging;

namespace Netfox.Extras;

/// <summary>
/// Request/accept model for spawning projectiles: the client spawns immediately and asks the authority, which accepts,
/// declines, or corrects. Not rollback based. Port of netfox.extras/weapon/network-weapon.gd.
/// </summary>
public partial class NetworkWeapon : Node
{
    /// <summary>The netfox stack this node uses; resolved when it enters the tree.</summary>
    public NetfoxContext Context { get; private set; } = NetfoxContext.Default;

    public override void _EnterTree()
    {
        Context = NetfoxContext.For(this);
    }

    private static readonly NetfoxLogger Logger = NetfoxLogger.ForExtras("NetworkWeapon");

    private readonly System.Collections.Generic.Dictionary<string, Node> _projectiles = new();
    private readonly System.Collections.Generic.Dictionary<string, Dictionary> _projectileData = new();
    private readonly List<(Node Projectile, Dictionary LocalData, Dictionary ResponseData, string Id)> _reconcileBuffer = new();
    private readonly RandomNumberGenerator _rng = new();
    private int _firedTick = -1;

    public override void _Ready()
    {
        _rng.Randomize();
        Context.NetworkTime.BeforeTickLoop += BeforeTickLoop;
    }

    public override void _ExitTree()
    {
        if (Context.NetworkTime is not null) Context.NetworkTime.BeforeTickLoop -= BeforeTickLoop;
    }

    public bool CanFire() => CanFireImpl();

    /// <summary>Spawn a projectile locally and request it from the authority. Returns null if the weapon cannot fire.</summary>
    public Node? Fire()
    {
        if (!CanFire()) return null;

        var id = GenerateId();
        var projectile = Spawn();
        if (projectile is null) return null;
        SaveProjectile(projectile, id);
        var data = _projectileData[id];

        if (!IsMultiplayerAuthority())
            RpcId(GetMultiplayerAuthority(), MethodName.RequestProjectile, id, Context.NetworkTime.Tick, data);
        else
            Rpc(MethodName.AcceptProjectile, id, Context.NetworkTime.Tick, data);

        Logger.Debug("Calling after fire hook for {0}", projectile.Name);
        _firedTick = Context.NetworkTime.Tick;
        AfterFire(projectile);

        return projectile;
    }

    public int GetFiredTick() => _firedTick;

    /// <summary>Whether the weapon can fire right now, e.g. based on cooldown.</summary>
    protected virtual bool CanFireImpl() => false;

    /// <summary>Whether <paramref name="peerId"/> may use this weapon. Checked by the authority.</summary>
    protected virtual bool CanPeerUse(int peerId) => true;

    protected virtual void AfterFire(Node projectile) { }

    /// <summary>Create the projectile node.</summary>
    protected virtual Node? Spawn() => null;

    /// <summary>Data describing the projectile, sent to peers.</summary>
    protected virtual Dictionary GetData(Node projectile) => new();

    protected virtual void ApplyData(Node projectile, Dictionary data) { }

    /// <summary>Whether the requesting peer and the authority agree closely enough to accept the projectile.</summary>
    protected virtual bool IsReconcilable(Node projectile, Dictionary requestData, Dictionary localData) => true;

    protected virtual void Reconcile(Node projectile, Dictionary localData, Dictionary remoteData) { }

    private void SaveProjectile(Node projectile, string id, Dictionary? data = null)
    {
        _projectiles[id] = projectile;
        projectile.Name = projectile.Name + " " + id;
        projectile.SetMultiplayerAuthority(GetMultiplayerAuthority());

        if (data is null || data.Count == 0)
            data = GetData(projectile);

        _projectileData[id] = data;
    }

    private void BeforeTickLoop()
    {
        foreach (var (projectile, localData, responseData, id) in _reconcileBuffer)
        {
            if (GodotObject.IsInstanceValid(projectile))
                Reconcile(projectile, localData, responseData);
            else
                Logger.Warning("Projectile {0} vanished by the time of reconciliation!", id);
        }
        _reconcileBuffer.Clear();
    }

    private string GenerateId(int length = 12, string charset = "abcdefghijklmnopqrstuvwxyz0123456789")
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = charset[_rng.RandiRange(0, charset.Length - 1)];
        return new string(chars);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestProjectile(string id, int tick, Dictionary requestData)
    {
        var sender = Multiplayer.GetRemoteSenderId();

        _firedTick = tick;
        if (!CanPeerUse(sender) || !CanFireImpl())
        {
            RpcId(sender, MethodName.DeclineProjectile, id);
            Logger.Error("Projectile {0} rejected! Peer {1} cannot use this weapon now", id, sender);
            return;
        }

        var projectile = Spawn();
        if (projectile is null)
        {
            RpcId(sender, MethodName.DeclineProjectile, id);
            return;
        }
        var localData = GetData(projectile);

        if (!IsReconcilable(projectile, requestData, localData))
        {
            projectile.QueueFree();
            RpcId(sender, MethodName.DeclineProjectile, id);
            Logger.Error("Projectile {0} rejected! Cannot reconcile states: [{1}, {2}]", id, requestData, localData);
            return;
        }

        SaveProjectile(projectile, id, localData);
        Rpc(MethodName.AcceptProjectile, id, tick, localData);
        AfterFire(projectile);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AcceptProjectile(string id, int tick, Dictionary responseData)
    {
        if (Multiplayer.GetUniqueId() == Multiplayer.GetRemoteSenderId())
            return; // Projectile is local, nothing to do

        Logger.Info("Accepting projectile {0} from {1}", id, Multiplayer.GetRemoteSenderId());

        if (_projectiles.TryGetValue(id, out var existing))
        {
            _reconcileBuffer.Add((existing, _projectileData[id], responseData, id));
        }
        else
        {
            _firedTick = tick;
            var projectile = Spawn();
            if (projectile is null) return;
            ApplyData(projectile, responseData);
            _projectileData.Remove(id);
            SaveProjectile(projectile, id, responseData);
            AfterFire(projectile);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void DeclineProjectile(string id)
    {
        if (!_projectiles.Remove(id, out var projectile)) return;
        if (GodotObject.IsInstanceValid(projectile)) projectile.QueueFree();
        _projectileData.Remove(id);
    }
}

/// <summary>NetworkWeapon that forwards its hooks to delegates, so Node2D/Node3D wrappers can host it. Port of network-weapon-proxy.gd.</summary>
internal partial class NetworkWeaponProxy : NetworkWeapon
{
    public Func<bool> CanFireCallback { get; set; } = () => false;
    public Func<int, bool> CanPeerUseCallback { get; set; } = _ => true;
    public Action<Node> AfterFireCallback { get; set; } = _ => { };
    public Func<Node?> SpawnCallback { get; set; } = () => null;
    public Func<Node, Dictionary> GetDataCallback { get; set; } = _ => new Dictionary();
    public Action<Node, Dictionary> ApplyDataCallback { get; set; } = (_, _) => { };
    public Func<Node, Dictionary, Dictionary, bool> IsReconcilableCallback { get; set; } = (_, _, _) => true;
    public Action<Node, Dictionary, Dictionary> ReconcileCallback { get; set; } = (_, _, _) => { };

    protected override bool CanFireImpl() => CanFireCallback();
    protected override bool CanPeerUse(int peerId) => CanPeerUseCallback(peerId);
    protected override void AfterFire(Node projectile) => AfterFireCallback(projectile);
    protected override Node? Spawn() => SpawnCallback();
    protected override Dictionary GetData(Node projectile) => GetDataCallback(projectile);
    protected override void ApplyData(Node projectile, Dictionary data) => ApplyDataCallback(projectile, data);
    protected override bool IsReconcilable(Node projectile, Dictionary requestData, Dictionary localData) => IsReconcilableCallback(projectile, requestData, localData);
    protected override void Reconcile(Node projectile, Dictionary localData, Dictionary remoteData) => ReconcileCallback(projectile, localData, remoteData);
}
