using Godot;

namespace Netfox.Tests;

/// <summary>
/// What a room of crates costs on the wire. Byte counts are stable between runs, unlike timings. The crates are real
/// rigid bodies with a NetworkObject, so the number is what a game pays: transform and both velocities per crate. Each
/// stack floats its crates in its own physics world without gravity, so moving ones keep moving and resting ones rest.
/// </summary>
public partial class BandwidthTests : HarnessSuite
{
    private const int Moving = 50;
    private const int Resting = 150;

    /// <summary>
    /// Bytes per second the host sends one peer for 50 moving and 150 resting crates; the host sends this to every
    /// guest. Measured at about 70.5 kB/s (564 kbit/s): 50 crates at 15 Hz dominate. A regression guard, not a target -
    /// quantization and deltas (see Deferred) are what would bring it down.
    /// </summary>
    private const double BudgetBytesPerSecond = 76_000;

    private static void AddCrates(NetfoxStack stack)
    {
        var world = new SubViewport { Name = "World", OwnWorld3D = true, Size = new Vector2I(2, 2) };
        stack.AddChild(world);
        for (var i = 0; i < Moving + Resting; i++)
        {
            var crate = new RigidBody3D
            {
                Name = $"Crate{i}",
                Position = new Vector3(i % 20 * 3, 0, i / 20 * 3),
                GravityScale = 0,
                LinearDamp = 0,
                LinearDampMode = RigidBody3D.DampMode.Replace,
                CanSleep = false,
            };
            crate.SetMultiplayerAuthority(1);
            crate.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One } });
            crate.AddChild(new NetworkObject { Name = "NetworkObject" });
            world.AddChild(crate);
            // Moving along Y, away from every other crate, so nothing collides and hands authority around
            if (i < Moving) crate.LinearVelocity = new Vector3(0, 1 + i % 5, 0);
        }
    }

    [Test]
    public async Task ARoomOfObjectsFitsTheBudget()
    {
        AddCrates(Host);
        AddCrates(Client);

        Expect.True(await WaitUntil(() => Client.Context.NetworkTime.IsInitialSyncDone(), 5), "client never synced");
        // Let identities settle: the first packets carry names until the ids are exchanged
        for (var i = 0; i < 60; i++) await NextFrame();

        // Object state only: the harness syncs clocks many times faster than a real session does
        var commands = Host.Context.NetworkCommandServer;
        commands.ResetSentCounts();
        var startTick = Host.Context.NetworkTime.Tick;
        await WaitUntil(() => Host.Context.NetworkTime.Tick >= startTick + 90, 10);
        var seconds = (Host.Context.NetworkTime.Tick - startTick) / (double)Host.Context.NetworkTime.Tickrate;

        var moving = Host.GetNode<RigidBody3D>("World/Crate0");
        Expect.True(moving.LinearVelocity.Y > 0.5f, $"the moving crates stopped: {moving.LinearVelocity}");

        var (bytes, packets) = commands.SentCounts.GetValueOrDefault(CommandIds.ObjectState);
        var perSecond = bytes / seconds;
        GD.Print($"BANDWIDTH moving={Moving} resting={Resting} bytes/s={perSecond:F0} packets/s={packets / seconds:F1} kbit/s={perSecond * 8 / 1000:F0}");

        Expect.True(perSecond < BudgetBytesPerSecond, $"{perSecond:F0} bytes/s over a budget of {BudgetBytesPerSecond}");
    }
}
