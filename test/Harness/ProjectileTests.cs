using Godot;

namespace CrackNet.Tests;

/// <summary>
/// Projectiles belong to their shooter, are spawned with their generated Spawn, and on other peers appear only when
/// playback reaches the tick they were fired at.
/// </summary>
public partial class ProjectileTests : HarnessSuite
{
    private const int Shooter = 2;
    private static readonly Vector3 Speed = new(10, 0, 0);
    private CrackNetStack _third = null!;
    private CrackNetStack[] _stacks = null!;

    public override async Task BeforeCase()
    {
        await base.BeforeCase();
        _third = AddPeer(3);
        _stacks = [Host, Client, _third];
        var synced = await WaitUntil(() => Client.Context.NetworkTime.IsInitialSyncDone() && _third.Context.NetworkTime.IsInitialSyncDone(), 5);
        Expect.True(synced, "peers never synced");
    }

    private HarnessBody Fire() => HarnessBody.Spawn(Transform3D.Identity, Speed, parent: Client);

    [Test]
    public async Task AProjectileAppearsOnOthersWhenPlaybackReachesItsFiringTick()
    {
        Network.LatencyMs = 40;
        var firedAt = Client.Context.NetworkTime.Tick;
        var fired = Fire();
        Expect.True(fired.Visible, "the shooter sees its own projectile at once");
        Expect.Equal(PlaybackState.Playing, fired.Object.PlaybackState);

        HarnessBody? onHost = null;
        var hiddenFrames = 0;
        for (var frame = 0; frame < 300; frame++)
        {
            await NextFrame();
            onHost ??= Host.GetNodeOrNull<HarnessBody>(fired.Name.ToString());
            if (onHost is null) continue;
            if (!onHost.Visible)
            {
                Expect.Equal(PlaybackState.Pending, onHost.Object.PlaybackState);
                hiddenFrames++;
                continue;
            }

            Expect.Equal(PlaybackState.Playing, onHost.Object.PlaybackState);
            var shown = Host.Context.NetworkObjectServer.Diagnostics.GetDisplayTick(Shooter) ?? -1;
            Expect.True(shown >= firedAt, $"shown at display tick {shown}, fired at {firedAt}");
            // At the muzzle, not already down range: the first frame shows the first sample
            var perTick = Speed.X / Host.Context.NetworkTime.Tickrate;
            Expect.True(onHost.Location.X < perTick * 4, $"first shown at {onHost.Location}");
            Expect.True(hiddenFrames > 0, "the host showed the projectile before any of its state");
            return;
        }

        Expect.True(false, $"never shown on the host: exists {onHost is not null}");
    }

    [Test]
    public async Task AProjectileKeepsItsMuzzleSamplesWhenStateBeatsSpawn()
    {
        Network.LatencyMs = 10;
        Network.ReliableExtraLatencyMs = 300;
        var fired = Fire();

        HarnessBody? onHost = null;
        for (var frame = 0; frame < 300; frame++)
        {
            await NextFrame();
            onHost ??= Host.GetNodeOrNull<HarnessBody>(fired.Name.ToString());
            if (onHost is not { Visible: true }) continue;

            // A tenth of a second of flight at most; losing the muzzle samples starts it 300 ms down range
            Expect.True(onHost.Location.X < Speed.X * 0.1f,
                $"first displayed at {onHost.Location.X:F2}, after the muzzle samples were sent");
            return;
        }

        Expect.True(false, "delayed projectile never became visible");
    }

    [Test]
    public async Task ObserversKeepAProjectileUntilItsDespawnSampleIsDisplayed()
    {
        Network.LatencyMs = 40;
        var fired = Fire();
        HarnessBody? onHost = null;
        Expect.True(await WaitUntil(() => (onHost = Host.GetNodeOrNull<HarnessBody>(fired.Name.ToString())) is { Visible: true }, 5),
            "projectile never became visible");

        var now = Client.Context.NetworkTime.Tick;
        var despawnTick = (now + 1) % NetworkObjectServer.StateIntervalTicks == 0 ? now + 1 : now + 2;
        Expect.True(fired.Object.Despawn());
        Expect.False(fired.Visible, "authority should hide a despawned projectile immediately");
        Expect.Equal(PlaybackState.Ending, fired.Object.PlaybackState);

        for (var frame = 0; frame < 180; frame++)
        {
            await NextFrame();
            onHost = Host.GetNodeOrNull<HarnessBody>(fired.Name.ToString());
            var shown = Host.Context.NetworkObjectServer.Diagnostics.GetDisplayTick(Shooter);
            if (shown is null || shown < despawnTick)
            {
                Expect.True(onHost is { Visible: true },
                    $"projectile disappeared at display tick {shown:F1}, before despawn {despawnTick}");
                continue;
            }

            if (onHost is { Visible: false }) Expect.Equal(PlaybackState.Ending, onHost.Object.PlaybackState);
            if (onHost is null or { Visible: false }) return;
            // The final sample is repeated for a while; hiding only on the last repeat left the shot hanging in the
            // air for the whole grace period
            Expect.True(shown < despawnTick + 1.5, $"projectile still visible at display tick {shown:F1}, despawned at {despawnTick}");
        }

        Expect.True(false, "projectile stayed visible after its despawn tick");
    }
}
