using Godot;
using Netfox.Core.Data;
using Netfox.Core.Logging;
using Netfox.Core.Serialization;
using Netfox.Internal;

namespace Netfox;

/// <summary>
/// Tracks network identities: nodes are referenced by scene path, replaced with compact numeric ids negotiated per peer.
/// Port of servers/network-identity-server.gd.
/// </summary>
public partial class NetworkIdentityServer : Node
{
    public static NetworkIdentityServer Instance { get; private set; } = null!;

    /// <summary>The stack this server belongs to; resolved when it enters the tree.</summary>
    public NetfoxContext Context { get; private set; } = NetfoxContext.Default;

    private static readonly NetfoxLogger Logger = NetfoxLogger.ForNetfox("NetworkIdentityServer");
    private const int WarnQueueSize = 128;

    private NetworkCommandServer? _commandServer;
    private NetworkCommandServer.Command _cmdIds = null!;

    private int _nextId;
    private readonly Dictionary<Node, NetworkIdentifier> _identifiers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, Dictionary<string, int>> _pushQueue = new();
    private readonly Dictionary<string, NetworkIdentifier> _identifierByName = new();
    private readonly Dictionary<int, Dictionary<int, NetworkIdentifier>> _identifierById = new();
    private readonly Dictionary<int, NetworkIdentifier> _identifierByLocalId = new();
    private bool _hasWarnedQueueLength;

    public NetworkIdentityServer() { }

    public NetworkIdentityServer(NetworkCommandServer commandServer)
    {
        _commandServer = commandServer;
    }

    public override void _EnterTree()
    {
        NetfoxRuntime.EnsureInitialized();
        Context = NetfoxContext.For(this);
        Context.NetworkIdentityServer ??= this;
        if (Context.IsDefault) Instance ??= this;
    }

    public override void _Ready()
    {
        _commandServer ??= Context.NetworkCommandServer;
        _cmdIds = _commandServer.RegisterCommandAt(CommandIds.Identities, HandleIds, MultiplayerPeer.TransferModeEnum.Reliable);

        if (Context.NetworkEvents is { Enabled: true } events)
        {
            events.OnPeerLeave += ErasePeer;
        }
        else
        {
            Multiplayer.PeerDisconnected += id => ErasePeer((int)id);
            if (!NetfoxSettings.Instance.SuppressIdentityPeerDisconnectedWarning)
                Logger.Warning(
                    "Using `multiplayer.peer_disconnected` to detect leaving peers. " +
                    "If the `multiplayer` instance changes, this will no longer work. " +
                    "Enable NetworkEvents, or call `NetworkIdentityServer.ErasePeer()` " +
                    "manually and disable this warning in the Project Settings. " +
                    "( Netfox > General > Supress Identity Peer Disconnected Warning)");
        }
    }

    public override void _ExitTree()
    {
        if (ReferenceEquals(Context.NetworkIdentityServer, this)) Context.NetworkIdentityServer = null!;
        if (Instance == this) Instance = null!;
    }

    /// <summary>Register a node so it can be referred to over the network. Must be registered with the same path on all peers.</summary>
    public void RegisterNode(Node node)
    {
        if (!node.IsInsideTree())
        {
            Logger.Error("Cannot register node {0} that is not inside tree!", node);
            return;
        }
        Register(node, IdentityPathOf(node));
    }

    /// <summary>
    /// Nodes are named by their path below the multiplayer root ("/root" unless the game sets its own), the same way
    /// RPCs address them. Upstream uses the absolute path (network-identity-server.gd:74), which assumes a single netfox
    /// stack per process; relative paths let two stacks in one tree agree on names.
    /// </summary>
    private string IdentityPathOf(Node node)
    {
        if (Multiplayer is SceneMultiplayer { RootPath: var rootPath }
            && !rootPath.IsEmpty
            && node.GetNodeOrNull(rootPath) is { } root)
            return root.GetPathTo(node);

        return node.GetPath();
    }

    public void DeregisterNode(Node node) => Deregister(node);

    /// <summary>Free all identity data associated with <paramref name="peer"/>.</summary>
    public void ErasePeer(int peer)
    {
        Logger.Debug("Erasing data for peer #{0}", peer);
        _pushQueue.Remove(peer);
        _identifierById.Remove(peer);
        foreach (var identifier in _identifiers.Values)
            identifier.EraseIdFor(peer);
    }

    /// <summary>Forgets the ids exchanged with peers, keeping the nodes registered locally.</summary>
    internal void ResetSession()
    {
        _pushQueue.Clear();
        foreach (var peer in _identifierById.Keys.ToList())
            foreach (var identifier in _identifiers.Values)
                identifier.EraseIdFor(peer);
        _identifierById.Clear();
        _hasWarnedQueueLength = false;
    }

    public void Clear()
    {
        _identifiers.Clear();
        _identifierByName.Clear();
        _identifierById.Clear();
        _identifierByLocalId.Clear();
        _nextId = 0;
    }

    /// <summary>Queue sending the numeric id of <paramref name="node"/> to <paramref name="peer"/>. Sent on the next FlushQueue.</summary>
    public bool QueueIdentifierFor(Node node, int peer)
    {
        var identifier = GetIdentifierOf(node);
        if (identifier is null) return false;
        QueueFor(identifier, peer);
        return true;
    }

    /// <summary>Broadcast all queued identities. Called automatically by NetworkTime.</summary>
    public void FlushQueue()
    {
        foreach (var (peer, ids) in _pushQueue)
            _cmdIds.Send(IdentityPacketSerializer.Serialize(ids), peer);
        _pushQueue.Clear();
    }

    public NetworkIdentifier? GetIdentifierOf(Node node) => _identifiers.GetValueOrDefault(node);

    /// <summary>Resolves a reference received from <paramref name="peer"/>. Ids are in our local id space; names queue our id for that peer.</summary>
    public NetworkIdentifier? ResolveReference(int peer, NetworkIdentityReference reference, bool allowQueue = true)
    {
        if (reference.HasId)
            return _identifierByLocalId.GetValueOrDefault(reference.Id);

        var identifier = _identifierByName.GetValueOrDefault(reference.FullName);
        if (allowQueue && identifier is not null)
            QueueFor(identifier, peer);
        return identifier;
    }

    private void QueueFor(NetworkIdentifier identifier, int peer)
    {
        if (!_pushQueue.TryGetValue(peer, out var queue))
            _pushQueue[peer] = queue = new Dictionary<string, int>();
        queue[identifier.FullName] = identifier.LocalId;

        if (!_hasWarnedQueueLength && queue.Count >= WarnQueueSize)
        {
            _hasWarnedQueueLength = true;
            Logger.Warning("Queue size for peer #{0} exceeded {1} - is the queue being flushed?", peer, queue.Count);
        }
    }

    private void Register(Node node, string path)
    {
        if (_identifiers.ContainsKey(node)) return;

        var identifier = new NetworkIdentifier(node, path, ++_nextId);
        _identifiers[node] = identifier;
        _identifierByName[identifier.FullName] = identifier;
        _identifierByLocalId[identifier.LocalId] = identifier;

        identifier.OnId += (peer, id) => UpdateIdCache(identifier, peer, id);
    }

    private void Deregister(Node node)
    {
        if (!_identifiers.Remove(node, out var identifier)) return;
        _identifierByName.Remove(identifier.FullName);
        _identifierByLocalId.Remove(identifier.LocalId);
        EraseFromIdCache(identifier);
    }

    private void UpdateIdCache(NetworkIdentifier identifier, int peer, int id)
    {
        if (!_identifierById.TryGetValue(peer, out var cache))
            _identifierById[peer] = cache = new Dictionary<int, NetworkIdentifier>();
        cache[id] = identifier;
    }

    private void EraseFromIdCache(NetworkIdentifier identifier)
    {
        foreach (var peer in identifier.KnownPeers.ToList())
        {
            if (!_identifierById.TryGetValue(peer, out var cache)) continue;
            cache.Remove(identifier.GetIdFor(peer));
            if (cache.Count == 0) _identifierById.Remove(peer);
        }
    }

    private void HandleIds(int sender, byte[] data)
    {
        foreach (var (fullName, id) in IdentityPacketSerializer.Deserialize(data))
        {
            Logger.Info("Received ID #{0} for {1} from #{2}", id, fullName, sender);
            var identifier = _identifierByName.GetValueOrDefault(fullName);
            if (identifier is null)
            {
                Logger.Debug("Received identifier for unknown object with full name {0}, id #{1}", fullName, id);
                continue;
            }
            identifier.SetIdFor(sender, id);
        }
    }
}
