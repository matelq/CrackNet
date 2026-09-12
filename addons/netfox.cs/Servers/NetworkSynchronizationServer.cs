using Godot;
using Netfox.Core.Collections;
using Netfox.Core.Logging;
using Netfox.Core.Serialization;
using Netfox.Internal;

namespace Netfox;

/// <summary>
/// Synchronizes rollback input, rollback state and synchronized state over the network, respecting visibility filters
/// and schemas, with optional diff states. Packets are sent per tick. Port of servers/network-synchronization-server.gd.
/// </summary>
public partial class NetworkSynchronizationServer : Node
{
    public static NetworkSynchronizationServer Instance { get; private set; } = null!;

    /// <summary>The stack this server belongs to; resolved when it enters the tree.</summary>
    public NetfoxContext Context { get; private set; } = NetfoxContext.Default;

    private static readonly NetfoxLogger Logger = NetfoxLogger.ForNetfox("NetworkSynchronizationServer");

    private NetworkCommandServer? _commandServer;
    private NetworkHistoryServer? _historyServer;
    private NetworkIdentityServer? _identityServer;
    private RollbackSimulationServer? _simulationServer;

    private readonly PropertyPool _rbInputProperties = new();
    private readonly PropertyPool _rbStateProperties = new();
    private readonly PropertyPool _rbOwnedInputProperties = new();
    private readonly PropertyPool _rbOwnedStateProperties = new();
    private readonly PropertyPool _syncStateProperties = new();
    private readonly PropertyPool _syncOwnedStateProperties = new();

    private readonly Dictionary<Node, PeerVisibilityFilter> _visibilityFilters = new(ReferenceEqualityComparer.Instance);

    private readonly bool _rbEnableDiffs = NetfoxSettings.Instance.RollbackEnableDiffStates;
    private readonly IntervalScheduler _rbFullScheduler = new(NetfoxSettings.Instance.RollbackFullStateInterval);
    private readonly int _inputRedundancy = Math.Max(1, NetfoxSettings.Instance.InputRedundancy);
    private readonly int _historyLimit = NetfoxSettings.Instance.RollbackHistoryLimit;

    private readonly Dictionary<int, HistoryBuffer<Snapshot>> _rbSentStateHistory = new();

    private Snapshot _lastSyncStateSent = new(0);
    private readonly bool _syncEnableDiffs = NetfoxSettings.Instance.StateSyncEnableDiffStates;
    private readonly IntervalScheduler _syncFullScheduler = new(NetfoxSettings.Instance.StateSyncFullStateInterval);

    // Very conservative packet size limit, source: https://stackoverflow.com/a/35697810
    private readonly int _maxPacketSize = NetfoxSettings.Instance.MaxSyncPacketSize;

    private readonly NetworkSchema _schemas = new(NetworkSchemas.Variant());

    private DenseSnapshotSerializer _denseSerializer = null!;
    private SparseSnapshotSerializer _sparseSerializer = null!;
    private RedundantSnapshotSerializer _redundantSerializer = null!;

    private NetworkCommandServer.Command _cmdFullState = null!;
    private NetworkCommandServer.Command _cmdDiffState = null!;
    private NetworkCommandServer.Command _cmdInput = null!;
    private NetworkCommandServer.Command _cmdFullSync = null!;
    private NetworkCommandServer.Command _cmdDiffSync = null!;

    /// <summary>When off, inputs only go to the authorities of the nodes they control. From netfox/rollback/enable_input_broadcast.</summary>
    public bool EnableInputBroadcast { get; set; } = NetfoxSettings.Instance.EnableInputBroadcast;

    /// <summary>Emitted when new input for a new subject was received.</summary>
    internal event Action<Snapshot>? OnInput;
    /// <summary>Emitted when state was received.</summary>
    internal event Action<Snapshot>? OnState;

    public NetworkSynchronizationServer() { }

    public NetworkSynchronizationServer(NetworkCommandServer? commandServer, NetworkHistoryServer? historyServer, NetworkIdentityServer? identityServer, RollbackSimulationServer? simulationServer)
    {
        _commandServer = commandServer;
        _historyServer = historyServer;
        _identityServer = identityServer;
        _simulationServer = simulationServer;
    }

    public override void _EnterTree()
    {
        NetfoxRuntime.EnsureInitialized();
        Context = NetfoxContext.For(this);
        Context.NetworkSynchronizationServer ??= this;
        if (Context.IsDefault) Instance ??= this;
    }

    public override void _Ready()
    {
        _commandServer ??= Context.NetworkCommandServer;
        _historyServer ??= Context.NetworkHistoryServer;
        _identityServer ??= Context.NetworkIdentityServer;
        _simulationServer ??= Context.RollbackSimulationServer;

        _denseSerializer = new DenseSnapshotSerializer(_schemas, _identityServer) { MaxPacketSize = _maxPacketSize };
        _sparseSerializer = new SparseSnapshotSerializer(_schemas, _identityServer) { MaxPacketSize = _maxPacketSize };
        _redundantSerializer = new RedundantSnapshotSerializer(_schemas, _identityServer) { MaxPacketSize = _maxPacketSize };

        _cmdFullState = _commandServer.RegisterCommandAt(CommandIds.FullState, HandleFullState, MultiplayerPeer.TransferModeEnum.Unreliable);
        _cmdDiffState = _commandServer.RegisterCommandAt(CommandIds.DiffState, HandleDiffState, MultiplayerPeer.TransferModeEnum.Unreliable);
        _cmdInput = _commandServer.RegisterCommandAt(CommandIds.Input, HandleInput, MultiplayerPeer.TransferModeEnum.Unreliable);
        _cmdFullSync = _commandServer.RegisterCommandAt(CommandIds.FullSyncState, HandleFullSync, MultiplayerPeer.TransferModeEnum.UnreliableOrdered);
        _cmdDiffSync = _commandServer.RegisterCommandAt(CommandIds.DiffSyncState, HandleDiffSync, MultiplayerPeer.TransferModeEnum.UnreliableOrdered);

        if (Context.NetworkEvents is { Enabled: true } events)
            events.OnPeerLeave += ErasePeer;
        else if (GodotObject.IsInstanceValid(Multiplayer))
            Multiplayer.PeerDisconnected += peer => ErasePeer((int)peer);
    }

    public override void _ExitTree()
    {
        if (ReferenceEquals(Context.NetworkSynchronizationServer, this)) Context.NetworkSynchronizationServer = null!;
        if (Instance == this) Instance = null!;
    }

    public void RegisterRollbackState(Node node, NodePath property)
    {
        _rbStateProperties.Add(node, property);
        if (node.IsMultiplayerAuthority()) _rbOwnedStateProperties.Add(node, property);
    }

    public void DeregisterRollbackState(Node node, NodePath property)
    {
        _rbStateProperties.Erase(node, property);
        _rbOwnedStateProperties.Erase(node, property);
    }

    public void RegisterRollbackInput(Node node, NodePath property)
    {
        _rbInputProperties.Add(node, property);
        if (node.IsMultiplayerAuthority()) _rbOwnedInputProperties.Add(node, property);
    }

    public void DeregisterRollbackInput(Node node, NodePath property)
    {
        _rbInputProperties.Erase(node, property);
        _rbOwnedInputProperties.Erase(node, property);
    }

    public void RegisterSyncState(Node node, NodePath property)
    {
        _syncStateProperties.Add(node, property);
        if (node.IsMultiplayerAuthority()) _syncOwnedStateProperties.Add(node, property);
    }

    public void DeregisterSyncState(Node node, NodePath property)
    {
        _syncStateProperties.Erase(node, property);
        _syncOwnedStateProperties.Erase(node, property);
    }

    public void RegisterSchema(Node node, NodePath property, NetworkSchemaSerializer serializer) => _schemas.Add(node, property, serializer);
    public void DeregisterSchema(Node node, NodePath property) => _schemas.Erase(node, property);
    public void DeregisterSchemaFor(Node node) => _schemas.EraseSubject(node);

    public void RegisterVisibilityFilter(Node node, PeerVisibilityFilter filter) => _visibilityFilters[node] = filter;
    public void DeregisterVisibilityFilter(Node node) => _visibilityFilters.Remove(node);

    /// <summary>Deregister every setting associated with <paramref name="node"/>.</summary>
    public void Deregister(Node node)
    {
        _rbStateProperties.EraseSubject(node);
        _rbInputProperties.EraseSubject(node);
        _rbOwnedStateProperties.EraseSubject(node);
        _rbOwnedInputProperties.EraseSubject(node);
        _syncStateProperties.EraseSubject(node);
        _syncOwnedStateProperties.EraseSubject(node);
        _visibilityFilters.Remove(node);
        _schemas.EraseSubject(node);
        // NOTE: _rbSentStateHistory may keep snapshots referencing the node; iterating them all would be slow and useless
    }

    /// <summary>Erase everything kept about <paramref name="peer"/>. Called by default when a peer leaves.</summary>
    public void ErasePeer(int peer) => _rbSentStateHistory.Remove(peer);

    internal PropertyPool OwnedRollbackStateProperties => _rbOwnedStateProperties;

    private HistoryBuffer<Snapshot> GetPeerSentHistory(int peer)
    {
        if (!_rbSentStateHistory.TryGetValue(peer, out var history))
            _rbSentStateHistory[peer] = history = new HistoryBuffer<Snapshot>(_historyLimit);
        return history;
    }

    internal Snapshot? GetLastSentRollbackState(int peer, int tick)
    {
        if (!_rbSentStateHistory.TryGetValue(peer, out var history)) return null;
        return history.TryGetLatestAt(tick, out var snapshot) ? snapshot : null;
    }

    internal void RememberSentRollbackState(int peer, Snapshot snapshot)
    {
        var reference = GetLastSentRollbackState(peer, snapshot.Tick);
        var remembered = reference?.Duplicate() ?? new Snapshot(snapshot.Tick);

        // The snapshot might be a diff, hence the merging
        foreach (var subject in snapshot.Subjects)
        {
            foreach (var (property, value) in snapshot.GetSubjectData(subject))
                remembered.SetProperty(subject, property, value);
            remembered.SetAuth(subject, snapshot.IsAuth(subject));
        }
        remembered.Tick = snapshot.Tick;

        GetPeerSentHistory(peer).SetAt(snapshot.Tick, remembered);
    }

    /// <summary>Snapshot to send to <paramref name="peer"/>: only visible subjects and their auth properties.</summary>
    internal Snapshot MakePeerSnapshot(Snapshot snapshot, int peer, PropertyPool properties)
    {
        var result = new Snapshot(snapshot.Tick);
        foreach (var subject in properties.Subjects)
        {
            if (!IsNodeVisibleTo(peer, subject)) continue;
            if (!snapshot.IsAuth(subject)) continue;

            var hasProperty = false;
            foreach (var property in properties.GetPropertiesOf(subject))
            {
                if (!snapshot.TryGetProperty(subject, property, out var value)) continue;
                result.SetProperty(subject, property, value);
                hasProperty = true;
            }

            if (hasProperty) result.SetAuth(subject, true);
        }
        return result;
    }

    private bool IsNodeVisibleTo(int peer, Node node)
        => !_visibilityFilters.TryGetValue(node, out var filter) || filter.GetVisiblePeers().Contains(peer);

    internal void SynchronizeInput(int tick)
    {
        if (_rbOwnedInputProperties.IsEmpty) return;

        var history = _historyServer ?? Context.NetworkHistoryServer;
        var simulation = _simulationServer ?? Context.RollbackSimulationServer;
        var notifiedPeers = new HashSet<int>();

        if (!EnableInputBroadcast)
        {
            // Send inputs to the peers owning nodes controlled by our inputs
            foreach (var inputSubject in _rbOwnedInputProperties.Subjects)
                foreach (var node in simulation.GetControlledBy(inputSubject))
                    notifiedPeers.Add(node.GetMultiplayerAuthority());
        }
        else
        {
            foreach (var peer in Multiplayer.GetPeers())
                notifiedPeers.Add(peer);
        }

        notifiedPeers.Remove(Multiplayer.GetUniqueId());

        var snapshots = new List<Snapshot>();
        for (var offset = 0; offset < _inputRedundancy; offset++)
        {
            var snapshot = history.GetRollbackInputSnapshot(tick - offset);
            if (snapshot is null) break;
            Logger.Trace("Submitting input: {0}", snapshot);
            snapshots.Add(snapshot);
        }

        Logger.Trace("Submitting input to peers: {0}", string.Join(", ", notifiedPeers));
        foreach (var peer in notifiedPeers)
        {
            var data = _redundantSerializer.WriteFor(peer, snapshots, _rbOwnedInputProperties);
            _cmdInput.Send(data, peer);
        }
    }

    internal void SynchronizeState(int tick)
    {
        if (_rbOwnedStateProperties.IsEmpty) return;

        var history = _historyServer ?? Context.NetworkHistoryServer;
        var snapshot = history.GetRollbackStateSnapshot(tick);
        if (snapshot is null || snapshot.IsEmpty) return;

        var isFull = _rbFullScheduler.IsNow() || !_rbEnableDiffs;
        var performance = Context.NetworkPerformance;

        foreach (var peer in Multiplayer.GetPeers())
        {
            var peerSnapshot = MakePeerSnapshot(snapshot, peer, _rbOwnedStateProperties);
            if (peerSnapshot.IsEmpty) continue;

            var reference = GetLastSentRollbackState(peer, tick);
            var peerIsFull = isFull || reference is null;

            if (peerIsFull)
            {
                foreach (var packet in _denseSerializer.WriteFor(peer, peerSnapshot, _rbOwnedStateProperties))
                    _cmdFullState.Send(packet, peer);

                RememberSentRollbackState(peer, peerSnapshot);
                performance?.PushFullStateProps(peerSnapshot.Size);
                performance?.PushSentStateProps(peerSnapshot.Size);
            }
            else
            {
                var diff = Snapshot.MakePatch(reference!, peerSnapshot);
                if (diff.IsEmpty) continue;

                foreach (var packet in _sparseSerializer.WriteFor(peer, diff, _rbOwnedStateProperties))
                    _cmdDiffState.Send(packet, peer);

                RememberSentRollbackState(peer, diff);
                performance?.PushFullStateProps(peerSnapshot.Size);
                performance?.PushSentStateProps(diff.Size);
            }
        }
    }

    internal void SynchronizeSyncState(int tick)
    {
        if (_syncOwnedStateProperties.IsEmpty) return;

        var history = _historyServer ?? Context.NetworkHistoryServer;
        var snapshot = history.GetSynchronizerStateSnapshot(tick);
        if (snapshot is null) return;

        var isFull = _syncFullScheduler.IsNow() || !_syncEnableDiffs;
        var performance = Context.NetworkPerformance;

        if (isFull)
        {
            foreach (var peer in Multiplayer.GetPeers())
            {
                foreach (var packet in _denseSerializer.WriteFor(peer, snapshot, _syncOwnedStateProperties, subject => IsNodeVisibleTo(peer, subject)))
                    _cmdFullSync.Send(packet, peer);

                performance?.PushFullStateProps(snapshot.Size);
                performance?.PushSentStateProps(snapshot.Size);
            }
        }
        else
        {
            var diff = Snapshot.MakePatch(_lastSyncStateSent, snapshot);
            foreach (var peer in Multiplayer.GetPeers())
            {
                foreach (var packet in _sparseSerializer.WriteFor(peer, diff, _syncOwnedStateProperties, subject => IsNodeVisibleTo(peer, subject)))
                    _cmdDiffSync.Send(packet, peer);

                performance?.PushFullStateProps(snapshot.Size);
                performance?.PushSentStateProps(diff.Size);
            }
        }

        // Shared instance, kept for diffing the next sync state
        _lastSyncStateSent = snapshot;
    }

    private void HandleInput(int sender, byte[] data)
    {
        var history = _historyServer ?? Context.NetworkHistoryServer;
        foreach (var snapshot in _redundantSerializer.ReadFrom(sender, _rbInputProperties, new ByteReader(data), isAuth: true))
        {
            snapshot.Sanitize(sender);
            Logger.Trace("Ingesting input: {0}", snapshot);
            if (history.MergeRollbackInput(snapshot))
                OnInput?.Invoke(snapshot);
        }
    }

    private void HandleFullState(int sender, byte[] data)
        => IngestState(sender, _denseSerializer.ReadFrom(sender, _rbStateProperties, new ByteReader(data), isAuth: true));

    private void HandleDiffState(int sender, byte[] data)
    {
        var diff = _sparseSerializer.ReadFrom(sender, _rbStateProperties, new ByteReader(data));
        Logger.Trace("Received diff state for @{0}", diff.Tick);
        IngestState(sender, diff);
    }

    private void HandleFullSync(int sender, byte[] data)
    {
        var snapshot = _denseSerializer.ReadFrom(sender, _syncStateProperties, new ByteReader(data), isAuth: true);
        snapshot.Sanitize(sender);
        (_historyServer ?? Context.NetworkHistoryServer).MergeSynchronizerState(snapshot);
        Logger.Trace("Ingested sync state: {0}", snapshot);
    }

    private void HandleDiffSync(int sender, byte[] data)
    {
        var snapshot = _sparseSerializer.ReadFrom(sender, _syncStateProperties, new ByteReader(data));
        snapshot.Sanitize(sender);
        (_historyServer ?? Context.NetworkHistoryServer).MergeSynchronizerState(snapshot);
        Logger.Trace("Ingested sync diff: {0}", snapshot);
    }

    private void IngestState(int sender, Snapshot snapshot)
    {
        snapshot.Sanitize(sender);
        (_historyServer ?? Context.NetworkHistoryServer).MergeRollbackState(snapshot);
        Logger.Trace("Ingested state: {0}", snapshot);
        OnState?.Invoke(snapshot);
    }
}
