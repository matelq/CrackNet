namespace CrackNet.Core.Data;

/// <summary>Either a compact numeric id or a full node name. Port of servers/data/network-identity-reference.gd.</summary>
public sealed class NetworkIdentityReference : IEquatable<NetworkIdentityReference>
{
    public string FullName { get; }
    public int Id { get; }

    private NetworkIdentityReference(string fullName, int id)
    {
        FullName = fullName;
        Id = id;
    }

    public static NetworkIdentityReference OfFullName(string fullName) => new(fullName, -1);
    public static NetworkIdentityReference OfId(int id) => new("", id);

    public bool HasId => Id >= 0;

    public bool Equals(NetworkIdentityReference? other)
        => other is not null && FullName == other.FullName && Id == other.Id;

    public override bool Equals(object? obj) => Equals(obj as NetworkIdentityReference);
    public override int GetHashCode() => HashCode.Combine(FullName, Id);

    public override string ToString() => HasId ? $"NetworkIdentityReference#{Id}" : $"NetworkIdentityReference({FullName})";
}
