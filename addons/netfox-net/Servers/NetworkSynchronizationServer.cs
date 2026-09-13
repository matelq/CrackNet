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
    private readonly int _maxInputRedundancy = Math.Max(1, NetfoxSettings.Instance.MaxInputRedundancy);

    /// <summary>What each sender has got through to us, so we can tell it what it no longer has to repeat.</summary>
    private readonly Dictionary<int, InputFrontier> _inputReceived = new();

    /// <summary>What each peer we send input to last told us it had. Absent means it has told us nothing yet.</summary>
    private readonly Dictionary<int, int> _inputAcknowledged = new();

    /// <summary>
    /// Ticks between acknowledgements. One per tick per peer would be half as many packets again on the host for four
    /// bytes each; a stale acknowledgement only costs a few repeated input ticks, which are patches against the
    /// newest and nearly free.
    /// </summary>
    private const int AckInterval = 4;
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
    private NetworkCommandServer.Command _cmdInputAck = null!;
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
        _cmdInputAck = _commandServer.RegisterCommandAt(CommandIds.InputAck, HandleInputAck, MultiplayerPeer.TransferModeEnum.Unreliable);
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
    public void ErasePeer(int peer)
    {
        _rbSentStateHistory.Remove(peer);
        _inputReceived.Remove(peer);
        _inputAcknowledged.Remove(peer);
    }

    /// <summary>Drops what was sent to whom, keeping registrations, schemas and visibility filters.</summary>
    internal void ResetSession()
    {
        _rbSentStateHistory.Clear();
        _inputReceived.Clear();
        _inputAcknowledged.Clear();
        _lastSyncStateSent = new Snapshot(0);
    }

    internal PropertyPool OwnedRollbackStateProperties => _rbOwnedStateProperties;

    /// <summary>
    /// Re-sorts every registered property into or out of the owned pools by what its node's authority is <i>now</i>.
    /// Called once per tick before anything is sent.
    /// <para>
    /// Registration sorted by authority once, and Godot has no signal for it changing, so a SetMultiplayerAuthority
    /// after that point left the pools describing the past: the peer that gained authority recorded state as real -
    /// the history server reads authority live - but never sent it, and the peer that lost it kept sending. Two
    /// servers disagreeing about one node, quietly. Anything that hands an object over at runtime hits this: a
    /// respawn onto another peer, possessing a character, a pickup whose truth should live with whoever holds it.
    /// </para>
    /// <para>
    /// A dozen dictionary lookups a tick for a room of four. Upstream has the same shape and the same gap
    /// (network-synchronization-server.gd:73), so this is inherited, and the fix lives here rather than in a helper
    /// callers would have to remember - the point is that nobody has to (netfox-net#45).
    /// </para>
    /// </summary>
    internal void ReconcileAuthority()
    {
        Reconcile(_rbStateProperties, _rbOwnedStateProperties);
        Reconcile(_rbInputProperties, _rbOwnedInputProperties);
        Reconcile(_syncStateProperties, _syncOwnedStateProperties);

        static void Reconcile(PropertyPool all, PropertyPool owned)
        {
            foreach (var subject in all.Subjects)
            {
                var isOwned = GodotObject.IsInstanceValid(subject) && subject.IsMultiplayerAuthority();
                if (isOwned == owned.HasSubject(subject)) continue;

                foreach (var property in all.GetPropertiesOf(subject))
                    if (isOwned) owned.Add(subject, property);
                    else owned.Erase(subject, property);
            }
        }
    }

    /// <summary>
    /// The value as this property's schema will deliver it, so what a peer records for itself is what every other
    /// peer will be told. Properties with no schema of their own are returned untouched, which is nearly all of them.
    /// </summary>
    internal Variant Quantize(Node subject, NodePath property, Variant value)
    {
        var serializer = _schemas.GetSerializer(subject, property);
        return ReferenceEquals(serializer, _schemas.Fallback) ? value : serializer.Quantize(value);
    }

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

    /// <summary>
    /// The diff baseline minus the subjects <paramref name="peer"/> cannot resolve yet, so those go out in full.
    /// <para>
    /// A peer acks a subject by sending back an id for it, which it can only do once it has resolved that subject's
    /// name - that is, once the node exists on its side. Until then it drops our frames, and diffing against a
    /// baseline it never received would leave it with a node missing every property that happens not to change, until
    /// the next full state. Upstream has no such guard (foxssake/netfox#563).
    /// </para>
    /// </summary>
    internal Snapshot WithoutUnacknowledgedSubjects(Snapshot reference, int peer)
    {
        var identityServer = _identityServer ?? Context.NetworkIdentityServer;
        if (identityServer is null) return reference;

        Snapshot? trimmed = null;
        foreach (var subject in reference.Subjects)
        {
            if (identityServer.GetIdentifierOf(subject)?.HasIdFor(peer) == true) continue;

            trimmed ??= reference.Duplicate();
            trimmed.EraseSubject(subject);
        }

        return trimmed ?? reference;
    }

    /// <summary>Snapshot to send to <paramref name="peer"/>: only visible subjects and their auth properties.</summary>
    internal Snapshot MakePeerSnapshot(Snapshot snapshot, int peer, PropertyPool properties, Func<Node, bool>? include = null)
    {
        var result = new Snapshot(snapshot.Tick);
        foreach (var subject in properties.Subjects)
        {
            if (include is not null && !include(subject)) continue;
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
                {
                    notifiedPeers.Add(node.GetMultiplayerAuthority());

                    // A peer owning another input node of the same subject simulates that subject too, so it needs
                    // this input as well. Upstream only notifies the state authority (foxssake/netfox#236), which
                    // leaves such a peer unable to simulate at all unless input broadcast is turned on for everyone.
                    foreach (var sibling in simulation.GetInputsOf(node))
                        notifiedPeers.Add(sibling.GetMultiplayerAuthority());
                }
        }
        else
        {
            foreach (var peer in Multiplayer.GetPeers())
                notifiedPeers.Add(peer);
        }

        notifiedPeers.Remove(Multiplayer.GetUniqueId());

        Logger.Trace("Submitting input to peers: {0}", string.Join(", ", notifiedPeers));
        foreach (var peer in notifiedPeers)
        {
            var snapshots = InputWindowFor(peer, tick, history);
            if (snapshots.Count == 0) continue;

            var data = _redundantSerializer.WriteFor(peer, snapshots, _rbOwnedInputProperties);
            _cmdInput.Send(data, peer);
        }
    }

    /// <summary>
    /// The input ticks to send one peer: everything it has not acknowledged, floored at the configured redundancy and
    /// capped so the packet still fits.
    /// <para>
    /// A fixed count is generous against independent loss and worth nothing against a burst - three in a row go
    /// missing 0.1% of the time at 10% loss, but a burst takes all three every time, and the authority is left
    /// predicting for the length of the outage. Sending what has not been acknowledged instead is what
    /// <a href="https://gafferongames.com/post/deterministic_lockstep/">Deterministic Lockstep</a> does, and it is
    /// smaller in the ordinary case as well as larger in the bad one: the floor only applies because an
    /// acknowledgement can itself be lost.
    /// </para>
    /// </summary>
    private List<Snapshot> InputWindowFor(int peer, int tick, NetworkHistoryServer history)
    {
        var count = _inputAcknowledged.TryGetValue(peer, out var acknowledged)
            ? Math.Clamp(tick - acknowledged, _inputRedundancy, _maxInputRedundancy)
            : _inputRedundancy;

        var snapshots = new List<Snapshot>(count);
        for (var offset = 0; offset < count; offset++)
        {
            var snapshot = history.GetRollbackInputSnapshot(tick - offset);
            if (snapshot is null) break;
            Logger.Trace("Submitting input: {0}", snapshot);
            snapshots.Add(snapshot);
        }
        return snapshots;
    }

    /// <summary>
    /// Tells every peer that has sent us input how far it has got through, so it can stop repeating what we already
    /// have. Called once per tick alongside <see cref="SynchronizeInput"/>, and rate limited from there.
    /// </summary>
    internal void AcknowledgeInput(int tick)
    {
        if (_inputReceived.Count == 0 || tick % AckInterval != 0) return;

        foreach (var (peer, frontier) in _inputReceived)
        {
            if (frontier.Tick < 0) continue;
            _cmdInputAck.Send(BitConverter.GetBytes(frontier.Tick), peer);
        }
    }

    private void HandleInputAck(int sender, byte[] data)
    {
        if (data.Length < sizeof(int)) return;
        var acknowledged = BitConverter.ToInt32(data);

        // Acknowledgements are unreliable and so can arrive out of order; an older one must never shrink the window
        if (!_inputAcknowledged.TryGetValue(sender, out var known) || acknowledged > known)
            _inputAcknowledged[sender] = acknowledged;
    }

    /// <summary>
    /// Sends every subject at the newest tick of the range it is authoritative for, rather than all of them at the
    /// newest tick of the range.
    /// <para>
    /// The distinction is the difference between working and not. State is only sent for subjects the sender is
    /// authoritative for, and a node driven by a remote peer's input is <i>predicted</i> at the newest tick - that
    /// peer's input for it is still a round trip away - so it is not authoritative there and nothing goes out for it.
    /// Sending only the newest tick therefore sent such a node nothing at all, ever, and the peer driving it never
    /// converged (netfox-net#35).
    /// </para>
    /// <para>
    /// Upstream sends every tick of the range instead (network-rollback.gd:429), which is correct but multiplies
    /// state traffic by the length of the range, every frame (#29). One tick per distinct answer is both: two, in a
    /// session where the host drives its own player and one remote player.
    /// </para>
    /// </summary>
    internal void SynchronizeStateRange(int fromTick, int toTick)
    {
        if (_rbOwnedStateProperties.IsEmpty) return;

        var history = _historyServer ?? Context.NetworkHistoryServer;
        var subjects = _rbOwnedStateProperties.Subjects;
        var newest = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance);

        for (var tick = toTick; tick >= fromTick && newest.Count < subjects.Count; tick--)
        {
            var snapshot = history.GetRollbackStateSnapshot(tick);
            if (snapshot is null) continue;

            foreach (var subject in subjects)
                if (!newest.ContainsKey(subject) && snapshot.IsAuth(subject))
                    newest[subject] = tick;
        }

        // Oldest first, so each peer's diff baseline is written in the order it will be read back in
        foreach (var tick in newest.Values.Distinct().OrderBy(value => value))
            SynchronizeState(tick, subject => newest.TryGetValue(subject, out var at) && at == tick);
    }

    internal void SynchronizeState(int tick, Func<Node, bool>? include = null)
    {
        if (_rbOwnedStateProperties.IsEmpty) return;

        var history = _historyServer ?? Context.NetworkHistoryServer;
        var snapshot = history.GetRollbackStateSnapshot(tick);
        if (snapshot is null || snapshot.IsEmpty) return;

        var isFull = _rbFullScheduler.IsNow() || !_rbEnableDiffs;
        var performance = Context.NetworkPerformance;

        foreach (var peer in Multiplayer.GetPeers())
        {
            var peerSnapshot = MakePeerSnapshot(snapshot, peer, _rbOwnedStateProperties, include);
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
                var diff = Snapshot.MakePatch(WithoutUnacknowledgedSubjects(reference!, peer), peerSnapshot);
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
        if (!_inputReceived.TryGetValue(sender, out var frontier))
            _inputReceived[sender] = frontier = new InputFrontier();

        foreach (var snapshot in _redundantSerializer.ReadFrom(sender, _rbInputProperties, new ByteReader(data), isAuth: true))
        {
            snapshot.Sanitize(sender);
            Logger.Trace("Ingesting input: {0}", snapshot);
            frontier.Receive(snapshot.Tick, _historyLimit);
            if (history.MergeRollbackInput(snapshot))
                OnInput?.Invoke(snapshot);
        }
    }

    /// <summary>
    /// The newest input tick from one sender below which nothing is missing, and the ticks seen above it.
    /// <para>
    /// The frontier is what gets acknowledged rather than the newest tick received, and the difference matters: if
    /// ticks 10, 11 and 13 arrive, acknowledging 13 tells the sender to stop repeating 12, which never came. An
    /// acknowledgement may be late, but it must never be wrong.
    /// </para>
    /// </summary>
    private sealed class InputFrontier
    {
        public int Tick { get; private set; } = -1;

        private readonly HashSet<int> _ahead = new();

        public void Receive(int tick, int historyLimit)
        {
            // The first input from a peer decides where counting starts: it joined mid-session and everything before
            // that tick was never owed to us
            if (Tick < 0) Tick = tick - 1;
            if (tick <= Tick) return;

            _ahead.Add(tick);
            while (_ahead.Remove(Tick + 1)) Tick++;

            // A tick that never arrives would hold the frontier back for good. Past the history limit the sender
            // could not resend it anyway, so it is gone rather than pending, and holding the window open for it only
            // costs bandwidth.
            if (tick - Tick <= historyLimit) return;
            Tick = tick - historyLimit;
            _ahead.RemoveWhere(at => at <= Tick);
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
