using Godot;
using Netfox.Core.Logging;
using Netfox.Internal;

namespace Netfox;

/// <summary>
/// Convenience multiplayer lifecycle events that survive MultiplayerAPI swaps, and automatic NetworkTime start/stop.
/// Port of network-events.gd.
/// </summary>
public partial class NetworkEvents : Node
{
    public static NetworkEvents Instance { get; private set; } = null!;

    /// <summary>The stack this server belongs to; resolved when it enters the tree.</summary>
    public NetfoxContext Context { get; private set; } = NetfoxContext.Default;

    /// <summary>(old, new)</summary>
    public event Action<MultiplayerApi?, MultiplayerApi?>? OnMultiplayerChange;
    public event Action? OnServerStart;
    public event Action? OnServerStop;
    /// <summary>(own peer id)</summary>
    public event Action<int>? OnClientStart;
    public event Action? OnClientStop;
    public event Action<int>? OnPeerJoin;
    public event Action<int>? OnPeerLeave;

    private bool _isServer;
    private bool _clientRunning;
    private bool _enabled;
    private MultiplayerApi? _multiplayer;
    private readonly Func<string> _peerIdTag;

    public NetworkEvents()
    {
        _peerIdTag = () => $"#{Multiplayer?.GetUniqueId() ?? 0}";
    }

    /// <summary>Events are only emitted while enabled. Initial value comes from netfox/events/enabled.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => SetEnabled(value);
    }

    public bool IsServer()
    {
        var mp = Multiplayer;
        if (mp is null) return false;
        var peer = mp.MultiplayerPeer;
        if (peer is null || peer is OfflineMultiplayerPeer) return false;
        if (peer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Connected) return false;
        return mp.IsServer();
    }

    public override void _EnterTree()
    {
        NetfoxRuntime.EnsureInitialized();
        Context = NetfoxContext.For(this);
        Context.NetworkEvents ??= this;
        if (Context.IsDefault) Instance ??= this;
    }

    public override void _Ready()
    {
        NetfoxLogger.RegisterTag(_peerIdTag, -99);

        Enabled = NetfoxSettings.Instance.EventsEnabled;

        // Automatically start ticking when entering multiplayer and stop when leaving
        OnServerStart += () => Context.NetworkTime.Start();
        OnServerStop += () => { Context.NetworkTime.Stop(); Context.ResetSession(); };
        OnClientStart += _ => Context.NetworkTime.Start();
        OnClientStop += () => { Context.NetworkTime.Stop(); Context.ResetSession(); };
    }

    public override void _ExitTree()
    {
        NetfoxLogger.FreeTag(_peerIdTag);
        if (ReferenceEquals(Context.NetworkEvents, this)) Context.NetworkEvents = null!;
        if (Instance == this) Instance = null!;
    }

    public override void _Process(double delta)
    {
        var current = Multiplayer;
        if (!ReferenceEquals(current, _multiplayer))
        {
            DisconnectHandlers(_multiplayer);
            ConnectHandlers(current);
            OnMultiplayerChange?.Invoke(_multiplayer, current);
            _multiplayer = current;
        }

        var isServer = IsServer();
        if (!_isServer && isServer)
        {
            _isServer = true;
            OnServerStart?.Invoke();
        }
        else if (_isServer && !isServer)
        {
            _isServer = false;
            OnServerStop?.Invoke();
        }
        else if (!isServer && !HasPeer())
        {
            // A client that leaves on its own never gets server_disconnected, and upstream then never emits
            // on_client_stop (network-events.gd:_process), leaving the servers holding the old session's data
            StopClient();
        }
    }

    private bool HasPeer()
        => GodotObject.IsInstanceValid(Multiplayer) && Multiplayer.HasMultiplayerPeer();

    /// <summary>Emits OnClientStop at most once per session, however the session ended.</summary>
    private void StopClient()
    {
        if (!_clientRunning) return;
        _clientRunning = false;
        OnClientStop?.Invoke();
    }

    private void ConnectHandlers(MultiplayerApi? mp)
    {
        if (mp is null) return;
        mp.ConnectedToServer += HandleConnectedToServer;
        mp.ServerDisconnected += HandleServerDisconnected;
        mp.PeerConnected += HandlePeerConnected;
        mp.PeerDisconnected += HandlePeerDisconnected;
    }

    private void DisconnectHandlers(MultiplayerApi? mp)
    {
        if (mp is null || !GodotObject.IsInstanceValid(mp)) return;
        mp.ConnectedToServer -= HandleConnectedToServer;
        mp.ServerDisconnected -= HandleServerDisconnected;
        mp.PeerConnected -= HandlePeerConnected;
        mp.PeerDisconnected -= HandlePeerDisconnected;
    }

    private void HandleConnectedToServer()
    {
        _clientRunning = true;
        OnClientStart?.Invoke(Multiplayer.GetUniqueId());
    }

    private void HandleServerDisconnected() => StopClient();
    private void HandlePeerConnected(long id) => OnPeerJoin?.Invoke((int)id);
    private void HandlePeerDisconnected(long id) => OnPeerLeave?.Invoke((int)id);

    private void SetEnabled(bool enable)
    {
        if (_enabled && !enable)
        {
            DisconnectHandlers(_multiplayer);
            _multiplayer = null;
        }
        if (!_enabled && enable)
        {
            _multiplayer = Multiplayer;
            ConnectHandlers(_multiplayer);
        }

        _enabled = enable;
        SetProcess(enable);
    }
}
