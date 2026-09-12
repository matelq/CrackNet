namespace Netfox.Core.Data;

/// <summary>Maps a subject to its local id and per-peer ids. Port of servers/data/network-identifier.gd.</summary>
public sealed class NetworkIdentifier<TSubject> where TSubject : class
{
    private readonly Dictionary<int, int> _ids = new(); // peer to id - local id intentionally not stored here

    public TSubject Subject { get; }
    public string FullName { get; }
    public int LocalId { get; }

    /// <summary>(peer, id)</summary>
    public event Action<int, int>? OnId;

    public NetworkIdentifier(TSubject subject, string fullName, int localId)
    {
        Subject = subject;
        FullName = fullName;
        LocalId = localId;
    }

    public bool HasIdFor(int peer) => _ids.ContainsKey(peer);

    public int GetIdFor(int peer) => _ids.TryGetValue(peer, out var id) ? id : -1;

    public void SetIdFor(int peer, int id)
    {
        if (_ids.TryGetValue(peer, out var existing) && existing != id)
            throw new InvalidOperationException($"ID for peer #{peer} is already set!");
        _ids[peer] = id;
        OnId?.Invoke(peer, id);
    }

    public void EraseIdFor(int peer) => _ids.Remove(peer);

    public IReadOnlyCollection<int> KnownPeers => _ids.Keys;

    public NetworkIdentityReference ReferenceFor(int peer)
        => HasIdFor(peer) ? NetworkIdentityReference.OfId(GetIdFor(peer)) : NetworkIdentityReference.OfFullName(FullName);

    public override string ToString() => $"NetworkIdentifier({FullName})";
}
