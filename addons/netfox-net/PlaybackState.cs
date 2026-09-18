namespace Netfox;

/// <summary>Where a replicated object is in its timeline on this peer. See <see cref="NetworkObject.PlaybackState"/>.</summary>
public enum PlaybackState
{
    /// <summary>Known here, but playback has not reached its first sample: not shown yet.</summary>
    Pending,

    /// <summary>Simulated here, or played back from its samples.</summary>
    Playing,

    /// <summary>Despawned: hidden, and freed once the other peers have played it to the end.</summary>
    Ending,
}
