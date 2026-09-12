using Godot;

namespace Netfox;

/// <summary>Decides which peers can see a synchronized node. Port of peer-visibility-filter.gd.</summary>
[GlobalClass]
public partial class PeerVisibilityFilter : Node
{
    public enum UpdateModeEnum
    {
        /// <summary>Only update visibility when manually triggered.</summary>
        Never,
        /// <summary>Update visibility when a peer joins or leaves.</summary>
        OnPeer,
        /// <summary>Update visibility before each tick loop.</summary>
        PerTickLoop,
        /// <summary>Update visibility before each network tick.</summary>
        PerTick,
        /// <summary>Update visibility after each rollback tick.</summary>
        PerRollbackTick,
    }

    [Export] public bool DefaultVisibility { get; set; } = true;

    [Export]
    public UpdateModeEnum UpdateMode
    {
        get => _updateMode;
        set => SetUpdateMode(value);
    }

    private readonly List<Func<int, bool>> _visibilityFilters = new();
    private readonly Dictionary<int, bool> _visibilityOverrides = new();
    private UpdateModeEnum _updateMode = UpdateModeEnum.OnPeer;
    private bool _handlersConnected;

    private readonly List<int> _visiblePeers = new();
    private List<int> _rpcTargetPeers = new();

    public void AddVisibilityFilter(Func<int, bool> filter)
    {
        if (!_visibilityFilters.Contains(filter)) _visibilityFilters.Add(filter);
    }

    public void RemoveVisibilityFilter(Func<int, bool> filter) => _visibilityFilters.Remove(filter);

    public void ClearVisibilityFilters() => _visibilityFilters.Clear();

    public bool GetVisibilityFor(int peer)
    {
        foreach (var filter in _visibilityFilters)
            if (!filter(peer)) return false;
        return _visibilityOverrides.TryGetValue(peer, out var visible) ? visible : DefaultVisibility;
    }

    /// <summary>Peer 0 sets the default visibility.</summary>
    public void SetVisibilityFor(int peer, bool visibility)
    {
        if (peer == 0) DefaultVisibility = visibility;
        else _visibilityOverrides[peer] = visibility;
    }

    public void UnsetVisibilityFor(int peer) => _visibilityOverrides.Remove(peer);

    /// <summary>Recomputes visible peers. Defaults to the current multiplayer peers.</summary>
    public void UpdateVisibility(IReadOnlyList<int>? peers = null)
    {
        peers ??= Multiplayer?.GetPeers() ?? Array.Empty<int>();

        _visiblePeers.Clear();
        foreach (var peer in peers)
            if (GetVisibilityFor(peer)) _visiblePeers.Add(peer);

        if (_visiblePeers.Count == peers.Count)
        {
            // Everyone is visible -> broadcast
            _rpcTargetPeers = [(int)MultiplayerPeer.TargetPeerBroadcast];
        }
        else if (_visiblePeers.Count == peers.Count - 1)
        {
            // Only a single peer is missing, exclude that
            foreach (var peer in peers)
            {
                if (_visiblePeers.Contains(peer)) continue;
                _rpcTargetPeers = [-peer];
                break;
            }
        }
        else
        {
            // Custom list, cannot optimize RPC call count; never include self
            _rpcTargetPeers = new List<int>(_visiblePeers);
            if (Multiplayer is not null) _rpcTargetPeers.Remove(Multiplayer.GetUniqueId());
        }
    }

    public IReadOnlyList<int> GetVisiblePeers() => _visiblePeers;

    /// <summary>Peer ids to pass to RpcId: a broadcast, a single exclusion (negative id), or an explicit list.</summary>
    public IReadOnlyList<int> GetRpcTargetPeers() => _rpcTargetPeers;

    public void SetUpdateMode(UpdateModeEnum mode)
    {
        if (_handlersConnected) DisconnectUpdateHandlers(_updateMode);
        _updateMode = mode;
        if (_handlersConnected) ConnectUpdateHandlers(_updateMode);
    }

    public UpdateModeEnum GetUpdateMode() => _updateMode;

    public override void _EnterTree()
    {
        ConnectUpdateHandlers(_updateMode);
        _handlersConnected = true;
        if (Multiplayer is not null) UpdateVisibility();
    }

    public override void _ExitTree()
    {
        DisconnectUpdateHandlers(_updateMode);
        _handlersConnected = false;
    }

    private void ConnectUpdateHandlers(UpdateModeEnum mode)
    {
        switch (mode)
        {
            case UpdateModeEnum.OnPeer:
                Multiplayer.PeerConnected += HandlePeer;
                Multiplayer.PeerDisconnected += HandlePeer;
                break;
            case UpdateModeEnum.PerTickLoop:
                NetworkTime.Instance.BeforeTickLoop += HandleTickLoop;
                break;
            case UpdateModeEnum.PerTick:
                NetworkTime.Instance.BeforeTick += HandleTick;
                break;
            case UpdateModeEnum.PerRollbackTick:
                NetworkRollback.Instance.AfterProcessTick += HandleRollbackTick;
                break;
        }
    }

    private void DisconnectUpdateHandlers(UpdateModeEnum mode)
    {
        switch (mode)
        {
            case UpdateModeEnum.OnPeer:
                if (GodotObject.IsInstanceValid(Multiplayer))
                {
                    Multiplayer.PeerConnected -= HandlePeer;
                    Multiplayer.PeerDisconnected -= HandlePeer;
                }
                break;
            case UpdateModeEnum.PerTickLoop:
                if (NetworkTime.Instance is not null) NetworkTime.Instance.BeforeTickLoop -= HandleTickLoop;
                break;
            case UpdateModeEnum.PerTick:
                if (NetworkTime.Instance is not null) NetworkTime.Instance.BeforeTick -= HandleTick;
                break;
            case UpdateModeEnum.PerRollbackTick:
                if (NetworkRollback.Instance is not null) NetworkRollback.Instance.AfterProcessTick -= HandleRollbackTick;
                break;
        }
    }

    private void HandlePeer(long _) => UpdateVisibility();
    private void HandleTickLoop() => UpdateVisibility();
    private void HandleTick(double _, int __) => UpdateVisibility();
    private void HandleRollbackTick(int _) => UpdateVisibility();
}
