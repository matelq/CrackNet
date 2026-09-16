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
    public async Task AHitDecidedByTheTargetAndOneByTheShooterConsumeTheProjectileOnce()
    {
        var fired = Fire("Rocket");
        HarnessBody? onThird = null;
        Expect.True(await WaitUntil(() => (onThird = _third.GetNodeOrNull<HarnessBody>("Rocket")) is { Visible: true }, 5), "never reached peer 3");

        var hits = new List<string>();
        fired.Object.EventReceived += (_, payload) =>
        {
            if (hits.Count > 0) return;
            hits.Add(payload.AsString());
            fired.QueueFree();
        };

        // Peer 3 sees the rocket hit its player; the shooter sees it hit an NPC. Both call it.
        onThird!.Object.SendToAuthority("player 3");
        fired.Object.SendToAuthority("npc");

        Expect.True(await WaitUntil(() => _stacks.All(stack => stack.GetNodeOrNull("Rocket") is null), 5),
            string.Join(", ", _stacks.Select(stack => $"{stack.Name}: {stack.GetNodeOrNull("Rocket") is not null}")));
        Expect.SequenceEqual(["npc"], hits);
    }
}
