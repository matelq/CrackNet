using Godot;

namespace Netfox.Tests;

/// <summary>
/// Projectiles belong to their shooter, are spawned with Godot's MultiplayerSpawner, and on other peers appear only when
/// playback reaches the tick they were fired at.
/// </summary>
public partial class ProjectileTests : HarnessSuite
{
    private const int Shooter = 2;
    private static readonly Vector3 Speed = new(10, 0, 0);
    private NetfoxStack _third = null!;
    private NetfoxStack[] _stacks = null!;

    public override async Task BeforeCase()
    {
        await base.BeforeCase();
        _third = AddPeer(3);
        _stacks = [Host, Client, _third];
        foreach (var stack in _stacks)
        {
            var spawner = new MultiplayerSpawner { Name = "ShooterSpawner", SpawnPath = new NodePath("..") };
            spawner.SpawnFunction = Callable.From((Variant data) => (Node)HarnessBody.Create(data.AsString(), Shooter, Speed));
            spawner.SetMultiplayerAuthority(Shooter);
            stack.AddChild(spawner);
        }

        var synced = await WaitUntil(() => Client.Context.NetworkTime.IsInitialSyncDone() && _third.Context.NetworkTime.IsInitialSyncDone(), 5);
        Expect.True(synced, "peers never synced");
    }

    private HarnessBody Fire(string name) => (HarnessBody)Client.GetNode<MultiplayerSpawner>("ShooterSpawner").Spawn(name);

    [Test]
    public async Task AProjectileAppearsOnOthersWhenPlaybackReachesItsFiringTick()
    {
        Network.LatencyMs = 40;
        var firedAt = Client.Context.NetworkTime.Tick;
        var fired = Fire("Bullet");
        Expect.True(fired.Visible, "the shooter sees its own projectile at once");

        HarnessBody? onHost = null;
        var hiddenFrames = 0;
        for (var frame = 0; frame < 300; frame++)
        {
            await NextFrame();
            onHost ??= Host.GetNodeOrNull<HarnessBody>("Bullet");
            if (onHost is null) continue;
            if (!onHost.Visible)
            {
                hiddenFrames++;
                continue;
            }

            var shown = Host.Context.NetworkObjectServer.GetDisplayTick(Shooter) ?? -1;
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
        Fire("DelayedSpawn");

        HarnessBody? onHost = null;
        for (var frame = 0; frame < 300; frame++)
        {
            await NextFrame();
            onHost ??= Host.GetNodeOrNull<HarnessBody>("DelayedSpawn");
            if (onHost is not { Visible: true }) continue;

            var perTick = Speed.X / Host.Context.NetworkTime.Tickrate;
            Expect.True(onHost.Location.X < perTick * 3,
                $"first displayed at {onHost.Location.X:F2}, after the muzzle samples were sent");
            return;
        }

        Expect.True(false, "delayed projectile never became visible");
    }

    [Test]
    public async Task ObserversKeepAProjectileUntilItsDespawnSampleIsDisplayed()
    {
        Network.LatencyMs = 40;
        var fired = Fire("TimedDespawn");
        HarnessBody? onHost = null;
        Expect.True(await WaitUntil(() => (onHost = Host.GetNodeOrNull<HarnessBody>("TimedDespawn")) is { Visible: true }, 5),
            "projectile never became visible");

        var now = Client.Context.NetworkTime.Tick;
        var despawnTick = (now + 1) % NetworkObjectServer.StateIntervalTicks == 0 ? now + 1 : now + 2;
        Expect.True(fired.Object.Despawn());
        Expect.False(fired.Visible, "authority should hide a despawned projectile immediately");

        for (var frame = 0; frame < 180; frame++)
        {
            await NextFrame();
            onHost = Host.GetNodeOrNull<HarnessBody>("TimedDespawn");
            var shown = Host.Context.NetworkObjectServer.GetDisplayTick(Shooter);
            if (shown is null || shown < despawnTick)
            {
                Expect.True(onHost is { Visible: true },
                    $"projectile disappeared at display tick {shown:F1}, before despawn {despawnTick}");
                continue;
            }

            if (onHost is null or { Visible: false }) return;
        }

        Expect.True(false, "projectile stayed visible after its despawn tick");
    }

    [Test]
    public async Task ProjectileAuthorityHitsOnlyTheFirstOfTwoPlayersInLine()
    {
        var hits = new List<string>();
        foreach (var stack in _stacks)
        {
            var near = HarnessBody.Spawn(stack, "Near", 3, location: new Vector3(1, 0, 0));
            var far = HarnessBody.Spawn(stack, "Far", 1, location: new Vector3(3, 0, 0));
            near.CountsTicks = far.CountsTicks = false;
            near.Object.Transferable = far.Object.Transferable = false;
            near.Object.EventReceived += (_, _) => hits.Add("near");
            far.Object.EventReceived += (_, _) => hits.Add("far");
        }
        Expect.True(await WaitUntil(() => Client.GetNode<HarnessBody>("Near").Visible
                                          && Client.GetNode<HarnessBody>("Far").Visible, 5),
            "shooter never displayed both targets");

        foreach (var stack in _stacks)
            HarnessProjectile.Spawn(stack, "LineShot", Shooter,
                (HarnessBody)stack.GetNode("Near"), (HarnessBody)stack.GetNode("Far"));

        Expect.True(await WaitUntil(() => hits.Count > 0, 5), "projectile never hit either player");
        for (var i = 0; i < 30; i++) await NextFrame();
        Expect.SequenceEqual(["near"], hits);
    }
}
