using Godot;

namespace Netfox.Tests;

/// <summary>A peer joining mid-session gets the world as it is: spawned objects, who simulates and holds what, and state.</summary>
public partial class LateJoinTests : HarnessSuite
{
    private const int Shooter = 2;
    private static readonly Vector3 Speed = new(2, 0, 0);

    [Test]
    public async Task ALateJoinerSeesWhoHoldsWhatAndWhatWasSpawned()
    {
        HarnessBody.Place(Host, "Crate", 1, Speed);
        var crate = HarnessBody.Place(Client, "Crate", 1, Speed);
        Expect.True(await WaitUntil(() => Client.Context.NetworkTime.IsInitialSyncDone(), 5), "client never synced");

        Expect.True(crate.Object.TryClaim());
        var bullet = HarnessBody.Spawn(Transform3D.Identity, Speed, parent: Client).Name.ToString();
        Expect.True(await WaitUntil(() => Host.GetNodeOrNull(bullet) is not null && ((HarnessBody)Host.GetNode("Crate")).Object.ClaimedBy == 2, 5),
            "host never saw the grab and the shot");

        var late = AddPeer(4);
        // The scene object shows up on the late peer a while after it connected: the host's word has to wait for it
        for (var i = 0; i < 15; i++) await NextFrame();
        var lateCrate = HarnessBody.Place(late, "Crate", 1, Speed);

        var caughtUp = await WaitUntil(() =>
            lateCrate.Object.Authority.Peer == 2 && lateCrate.Object.ClaimedBy == 2
            && lateCrate.Ticks > crate.Ticks - 10 && lateCrate.Visible
            && late.GetNodeOrNull<HarnessBody>(bullet) is { Visible: true }, 5);

        Expect.True(caughtUp,
            $"crate auth {lateCrate.Object.Authority.Peer} owner {lateCrate.Object.ClaimedBy} ticks {lateCrate.Ticks}/{crate.Ticks} visible {lateCrate.Visible}, " +
            $"bullet {late.GetNodeOrNull<HarnessBody>(bullet)?.Visible}");
    }

    [Test]
    public async Task TransferableChangesReachALateJoiner()
    {
        var onHost = HarnessBody.Place(Host, "Locked", 1);
        HarnessBody.Place(Client, "Locked", 1);
        await NextFrame();
        onHost.Object.Transferable = false;

        var late = AddPeer(4);
        for (var i = 0; i < 10; i++) await NextFrame();
        var lateBody = HarnessBody.Place(late, "Locked", 1);

        Expect.True(await WaitUntil(() => !lateBody.Object.Transferable, 3),
            "late joiner kept the scene default instead of the host's authority record");
    }
}
