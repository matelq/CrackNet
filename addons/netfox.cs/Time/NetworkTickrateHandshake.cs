using Godot;
using Netfox.Core.Logging;
using Netfox.Internal;

namespace Netfox;

public enum TickrateMismatchAction
{
    /// <summary>Emit a warning on tickrate mismatch.</summary>
    Warn = 0,
    /// <summary>Disconnect the peer on mismatch. Enforced by the host.</summary>
    Disconnect = 1,
    /// <summary>Adjust the local tickrate to the host on mismatch.</summary>
    Adjust = 2,
    /// <summary>Emit OnTickrateMismatch on mismatch, on both host and client.</summary>
    Signal = 3,
}

/// <summary>Exchanges the configured tickrate with the host when peers join. Port of time/network-tickrate-handshake.gd.</summary>
public partial class NetworkTickrateHandshake : Node
{
    private static readonly NetfoxLogger Logger = NetfoxLogger.ForNetfox("NetworkTickrateHandshake");

    public TickrateMismatchAction MismatchAction { get; set; }
        = (TickrateMismatchAction)Settings.GetInt("netfox/time/tickrate_mismatch_action", (int)TickrateMismatchAction.Warn);

    /// <summary>(peer, tickrate)</summary>
    public event Action<int, int>? OnTickrateMismatch;

    private bool _listening;

    public override void _Ready()
    {
        Name = "NetworkTickrateHandshake";
    }

    /// <summary>Run the handshake: broadcast tickrate, and send it to every joining peer. Called by NetworkTime.</summary>
    public void Run()
    {
        if (IsAuthority())
        {
            Rpc(MethodName.SubmitTickrate, NetworkTime.Instance.Tickrate);
            if (!_listening)
            {
                Multiplayer.PeerConnected += HandleNewPeer;
                _listening = true;
            }
        }
        else
        {
            RpcId(1, MethodName.SubmitTickrate, NetworkTime.Instance.Tickrate);
        }
    }

    public void Stop()
    {
        if (_listening && GodotObject.IsInstanceValid(Multiplayer))
            Multiplayer.PeerConnected -= HandleNewPeer;
        _listening = false;
    }

    private void HandleNewPeer(long peer)
    {
        if (IsAuthority())
            RpcId(peer, MethodName.SubmitTickrate, NetworkTime.Instance.Tickrate);
    }

    private void HandleTickrateMismatch(int peer, int tickrate)
    {
        var networkTime = NetworkTime.Instance;
        switch (MismatchAction)
        {
            case TickrateMismatchAction.Warn:
                Logger.Warning(
                    "Local tickrate {0}tps differs from tickrate of peer #{1} at {2}tps! " +
                    "Make sure that tickrates are correctly configured in the Project settings! " +
                    "See netfox/Time/Tickrate.", networkTime.Tickrate, peer, tickrate);
                break;
            case TickrateMismatchAction.Disconnect:
                if (IsAuthority())
                {
                    Logger.Warning("Tickrate of peer #{0} at {1}tps differs from expected {2}tps! Disconnecting.", peer, tickrate, networkTime.Tickrate);
                    Multiplayer.MultiplayerPeer.DisconnectPeer(peer);
                }
                break;
            case TickrateMismatchAction.Adjust:
                if (!IsAuthority())
                {
                    Logger.Info("Local tickrate {0}tps differs from tickrate of host at {1}tps! Adjusting.", networkTime.Tickrate, tickrate);
                    networkTime.Tickrate = tickrate;
                }
                break;
            case TickrateMismatchAction.Signal:
                OnTickrateMismatch?.Invoke(peer, tickrate);
                break;
        }
    }

    /// <summary>Overridable to ease testing; pretending to be a client is messy from a unit test.</summary>
    protected virtual bool IsAuthority() => Multiplayer.IsServer();

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SubmitTickrate(int tickrate)
    {
        var sender = Multiplayer.GetRemoteSenderId();
        Logger.Debug("Received tickrate {0} from peer {1}", tickrate, sender);
        if (tickrate != NetworkTime.Instance.Tickrate)
            HandleTickrateMismatch(sender, tickrate);
    }

    /// <summary>Test hook: feed a tickrate as if received from <paramref name="sender"/>.</summary>
    internal void ReceiveTickrate(int sender, int tickrate)
    {
        if (tickrate != NetworkTime.Instance.Tickrate)
            HandleTickrateMismatch(sender, tickrate);
    }
}
