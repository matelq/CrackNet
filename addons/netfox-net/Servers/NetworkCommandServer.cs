using Godot;
using Netfox.Core.Logging;
using Netfox.Core.Serialization;
using Netfox.Internal;

namespace Netfox;

/// <summary>Well-known command ids. Explicit so they do not depend on autoload order.</summary>
public static class CommandIds
{
    public const int Ping = 0;
    public const int Pong = 1;
    public const int RequestTime = 2;
    public const int SetTime = 3;
    public const int ObjectState = 4;
    public const int ObjectAuthority = 5;
    public const int ObjectEvent = 6;
    public const int Identities = 9;

    /// <summary>First id handed out by RegisterCommand for user commands.</summary>
    public const int FirstUserCommand = 32;
}

/// <summary>
/// Transmits commands over the network: a single id byte plus raw binary data, either over RPC (default)
/// or as raw SceneMultiplayer packets. Port of servers/network-command-server.gd.
/// </summary>
public partial class NetworkCommandServer : Node
{
    public static NetworkCommandServer Instance { get; private set; } = null!;

    /// <summary>The stack this server belongs to; resolved when it enters the tree.</summary>
    public NetfoxContext Context { get; private set; } = NetfoxContext.Default;

    private static readonly NetfoxLogger Logger = NetfoxLogger.ForNetfox("NetworkCommandServer");

    /// <summary>Prefix of raw command packets: NUL, n, f.</summary>
    public static readonly byte[] PacketPrefix = [0, 78, 70];

    private readonly RpcCommandTransport _rpcTransport = new();
    private readonly PacketCommandTransport _packetTransport = new(PacketPrefix);
    private readonly Dictionary<int, Command> _commands = new();
    private readonly bool _useRaw = NetfoxSettings.Instance.UseRawCommands;
    private int _nextIdx = CommandIds.FirstUserCommand;

    public override void _EnterTree()
    {
        NetfoxRuntime.EnsureInitialized();
        Context = NetfoxContext.For(this);
        Context.NetworkCommandServer ??= this;
        if (Context.IsDefault) Instance ??= this;
    }

    public override void _Ready()
    {
        AddChild(_rpcTransport, true);
        AddChild(_packetTransport, true);

        _rpcTransport.OnReceive += HandleCommand;
        _packetTransport.OnReceive += HandleCommand;
    }

    public override void _ExitTree()
    {
        if (ReferenceEquals(Context.NetworkCommandServer, this)) Context.NetworkCommandServer = null!;
        if (Instance == this) Instance = null!;
    }

    /// <summary>Register a command at the next available id.</summary>
    public Command RegisterCommand(Action<int, byte[]> handler, MultiplayerPeer.TransferModeEnum mode = MultiplayerPeer.TransferModeEnum.Reliable, int channel = 0)
    {
        while (_commands.ContainsKey(_nextIdx)) _nextIdx++;
        return RegisterCommandAt(_nextIdx++, handler, mode, channel);
    }

    /// <summary>Register a command at a specific id. Registering the same id twice is an error.</summary>
    public Command RegisterCommandAt(int idx, Action<int, byte[]> handler, MultiplayerPeer.TransferModeEnum mode = MultiplayerPeer.TransferModeEnum.Reliable, int channel = 0)
    {
        if (idx is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(idx), "Command ids must fit in a byte");
        if (_commands.ContainsKey(idx))
            Logger.Error("Command #{0} is already taken, overwriting!", idx);

        var command = new Command(this, idx, handler, mode, channel);
        _commands[idx] = command;
        _nextIdx = Math.Max(_nextIdx, idx + 1);
        return command;
    }

    public virtual void SendCommand(int idx, byte[] data, int targetPeer = 0, MultiplayerPeer.TransferModeEnum mode = MultiplayerPeer.TransferModeEnum.Reliable, int channel = 0)
    {
        var counted = _sent.GetValueOrDefault(idx);
        _sent[idx] = (counted.Bytes + data.Length, counted.Packets + 1);

        if (_useRaw) _packetTransport.Send(idx, data, targetPeer, mode, channel);
        else _rpcTransport.Send(idx, data, targetPeer, mode, channel);
    }

    /// <summary>
    /// Payload bytes and packets sent per command id since the last <see cref="ResetSentCounts"/>. Transport framing
    /// is not included, so this says what netfox asked for rather than what went on the wire.
    /// <para>
    /// It exists because a total cannot answer the question that matters when something grows: which command grew.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<int, (long Bytes, long Packets)> SentCounts => _sent;

    public void ResetSentCounts() => _sent.Clear();

    private readonly Dictionary<int, (long Bytes, long Packets)> _sent = new();

    /// <summary>True if <paramref name="packet"/> is a command packet. Always true when commands go over RPC.</summary>
    public bool IsCommandPacket(ReadOnlySpan<byte> packet) => !_useRaw || PacketCommandTransport.IsCommandPacket(PacketPrefix, packet);

    public byte[] GetCommandPacketPrefix() => PacketPrefix;

    private void HandleCommand(int sender, int idx, byte[] data)
    {
        if (!_commands.TryGetValue(idx, out var command))
        {
            Logger.Error("Received unknown command #{0}!", idx);
            return;
        }
        command.Handle(sender, data);
    }

    /// <summary>A registered networked command. Obtained from RegisterCommand.</summary>
    public sealed class Command
    {
        private readonly NetworkCommandServer _server;
        private readonly Action<int, byte[]> _handler;

        public int Id { get; }
        public MultiplayerPeer.TransferModeEnum Mode { get; }
        public int Channel { get; }

        internal Command(NetworkCommandServer server, int id, Action<int, byte[]> handler, MultiplayerPeer.TransferModeEnum mode, int channel)
        {
            _server = server;
            Id = id;
            _handler = handler;
            Mode = mode;
            Channel = channel;
        }

        /// <summary>Send to <paramref name="targetPeer"/>, or to everyone when 0.</summary>
        public void Send(byte[] data, int targetPeer = 0) => _server.SendCommand(Id, data, targetPeer, Mode, Channel);

        internal void Handle(int sender, byte[] data) => _handler(sender, data);
    }
}

internal abstract partial class CommandTransport : Node
{
    /// <summary>(sender, command id, data)</summary>
    public event Action<int, int, byte[]>? OnReceive;

    protected void Receive(int sender, int idx, byte[] data) => OnReceive?.Invoke(sender, idx, data);

    public abstract void Send(int idx, byte[] data, int targetPeer, MultiplayerPeer.TransferModeEnum mode, int channel);
}

internal partial class PacketCommandTransport : CommandTransport
{
    private readonly byte[] _prefix;

    public PacketCommandTransport() : this(NetworkCommandServer.PacketPrefix) { }

    public PacketCommandTransport(byte[] prefix)
    {
        _prefix = prefix;
    }

    public override void _Ready()
    {
        if (Multiplayer is SceneMultiplayer sceneMultiplayer)
            sceneMultiplayer.PeerPacket += HandlePacket;
    }

    public override void Send(int idx, byte[] data, int targetPeer, MultiplayerPeer.TransferModeEnum mode, int channel)
    {
        var buffer = new ByteWriter(_prefix.Length + 1 + data.Length);
        buffer.PutData(_prefix);
        buffer.PutU8((byte)idx);
        buffer.PutData(data);
        (Multiplayer as SceneMultiplayer)?.SendBytes(buffer.ToArray(), targetPeer, mode, channel);
    }

    public static bool IsCommandPacket(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> packet)
        => packet.Length >= prefix.Length && packet.Slice(0, prefix.Length).SequenceEqual(prefix);

    private void HandlePacket(long peer, byte[] packet)
    {
        if (!IsCommandPacket(_prefix, packet)) return;
        var idx = packet[_prefix.Length];
        var data = packet.AsSpan(_prefix.Length + 1).ToArray();
        Receive((int)peer, idx, data);
    }
}

internal partial class RpcCommandTransport : CommandTransport
{
    public override void Send(int idx, byte[] data, int targetPeer, MultiplayerPeer.TransferModeEnum mode, int channel)
    {
        switch (mode)
        {
            case MultiplayerPeer.TransferModeEnum.Unreliable:
                RpcId(targetPeer, MethodName.SubmitUnreliable, idx, data);
                break;
            case MultiplayerPeer.TransferModeEnum.UnreliableOrdered:
                RpcId(targetPeer, MethodName.SubmitUnreliableOrdered, idx, data);
                break;
            default:
                RpcId(targetPeer, MethodName.SubmitReliable, idx, data);
                break;
        }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void SubmitUnreliable(int idx, byte[] data) => Receive(Multiplayer.GetRemoteSenderId(), idx, data);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void SubmitUnreliableOrdered(int idx, byte[] data) => Receive(Multiplayer.GetRemoteSenderId(), idx, data);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SubmitReliable(int idx, byte[] data) => Receive(Multiplayer.GetRemoteSenderId(), idx, data);
}
