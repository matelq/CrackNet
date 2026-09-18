namespace CrackNet;

/// <summary>A property a NetworkObject replicates: its path relative to the declaring node, and whether playback blends it.</summary>
public readonly record struct SyncedProperty(string Path, bool Interpolate);

/// <summary>
/// Declares the synced properties of a node. Implemented by the source generator for every partial type with
/// <c>[Synced]</c> properties; it can also be implemented by hand.
/// </summary>
public interface ISyncedProperties
{
    IEnumerable<SyncedProperty> GetSyncedProperties();
}
