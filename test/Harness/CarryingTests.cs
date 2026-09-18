// Bodies built in code: CRN006 has nothing to check here
#pragma warning disable CRN006

using Godot;

namespace CrackNet.Tests;

/// <summary>
/// An item carried at an anchor of another object: while attached it sends no transform, and every peer places it on
/// its own copy of the anchor after that peer's animation, so a hand and what it holds cannot drift apart. See
/// docs/design/distributed-authority.md, "Carrying and animation".
/// </summary>
public partial class CarryingTests : HarnessSuite
{
    public override async Task BeforeCase()
    {
        await base.BeforeCase();
        Expect.True(await WaitUntil(() => Client.Context.NetworkTime.IsInitialSyncDone(), 5), "client never synced");
    }

    private async Task<CrackNetStack> ThirdPeer()
    {
        var third = AddPeer(3);
        Expect.True(await WaitUntil(() => third.Context.NetworkTime.IsInitialSyncDone(), 5), "third peer never synced");
        return third;
    }

    /// <summary>Placed on the anchor after the animation, an item is exactly there; anything else is a lag.</summary>
    private const float CarryTolerance = 0.02f;

    /// <summary>
    /// Peer 2 walks with a crate in a hand that its animation bobs; the host and a third peer watch. Measured per
    /// rendered frame at the end of the frame on every peer, the carrier's own included: the distance between the
    /// crate and that peer's own hand. Played back from its own samples, the crate trails the hand by the playback
    /// delay (0.56 m here); placed after the carrier's animation, it does not.
    /// </summary>
    [Test]
    public async Task ACarriedItemStaysAtTheAnchorDuringAnAnimatedWalk()
    {
        Network.LatencyMs = 50;
        var stacks = new[] { Host, Client, await ThirdPeer() };
        // Crates before carriers: added first, a crate's frame runs before the carrier's animation, which is the order
        // the placement has to survive
        var crates = stacks.Select(stack => HarnessWorld.Crate(stack, "Crate", new Vector3(0, 0.5f, 3))).ToArray();
        var carriers = stacks.Select(stack => HarnessWorld.Walker(stack, 2, new Vector3(0, 1, 0), new Vector3(2, 0, 0))).ToArray();
        var hands = carriers.Select(HarnessWorld.BobbingHand).ToArray();
        for (var i = 0; i < 20; i++) await NextFrame();

        Expect.True(carriers[1].TryAttach(crates[1], hands[1]), "the carrier could not attach the crate");
        Expect.True(await WaitUntil(() => crates.Select((crate, i) => crate.AttachedTo == carriers[i]).All(attached => attached), 3),
            "not every peer shows the crate in the hand: " + string.Join(", ", crates.Select(crate => crate.AttachedTo?.Name ?? "free")));
        for (var i = 0; i < 20; i++) await NextFrame();

        var worst = new float[stacks.Length];
        var handLow = new float[stacks.Length];
        var handHigh = new float[stacks.Length];
        Array.Fill(handLow, float.MaxValue);
        Array.Fill(handHigh, float.MinValue);
        var startX = carriers.Select(carrier => carrier.GlobalPosition.X).ToArray();
        var drawn = new DrawnFrame();
        AddChild(drawn);
        drawn.Drawn += () =>
        {
            for (var i = 0; i < stacks.Length; i++)
            {
                worst[i] = Mathf.Max(worst[i], crates[i].GlobalPosition.DistanceTo(hands[i].GlobalPosition));
                handLow[i] = Mathf.Min(handLow[i], hands[i].GlobalPosition.Y);
                handHigh[i] = Mathf.Max(handHigh[i], hands[i].GlobalPosition.Y);
            }
        };
        for (var seconds = 0.0; seconds < 3; seconds += GetProcessDeltaTime()) await NextFrame();
        drawn.QueueFree();

        var report = string.Join("; ", stacks.Select((stack, i) =>
            $"{stack.Name}: crate off the hand by {worst[i]:F3} m, hand bobbed {handHigh[i] - handLow[i]:F2} m, walked {carriers[i].GlobalPosition.X - startX[i]:F1} m"));
        GD.Print("CARRIED WALK " + report);
        // What the check measures has to have happened: a hand that did not bob and a carrier that did not walk
        // would pass with the item nailed anywhere
        for (var i = 0; i < stacks.Length; i++)
        {
            Expect.True(handHigh[i] - handLow[i] > 0.4f, $"{stacks[i].Name}'s hand did not bob: " + report);
            Expect.True(carriers[i].GlobalPosition.X - startX[i] > 2, $"{stacks[i].Name}'s carrier did not walk: " + report);
        }
        Expect.True(worst.All(distance => distance < CarryTolerance), "the carried crate leaves the hand: " + report);
    }

    /// <summary>
    /// The host's record of the claim arrives a playback delay before the carrier's hand reaches the crate on an
    /// observer's screen. The observer shows the crate in the hand when its playback of the carrier reaches the
    /// attach tick, not when the record lands, and shows it free again at the detach tick. A counter bumped in the
    /// same tick as each change (the one-shot pattern for a grab or throw animation) changes in the same frame.
    /// </summary>
    [Test]
    public async Task AttachAndDetachSwitchAtTheCarriersDisplayTick()
    {
        Network.LatencyMs = 100;
        var crates = new[] { HarnessWorld.Crate(Host, "Crate", new Vector3(0, 0.5f, 3)), HarnessWorld.Crate(Client, "Crate", new Vector3(0, 0.5f, 3)) };
        var carriers = new[] { HarnessWorld.Walker(Host, 2, new Vector3(0, 1, 0), Vector3.Zero), HarnessWorld.Walker(Client, 2, new Vector3(0, 1, 0), Vector3.Zero) };
        var hands = carriers.Select(HarnessWorld.BobbingHand).ToArray();
        for (var i = 0; i < 30; i++) await NextFrame();

        // Per frame on the host: what it shows for peer 2, and what it knows
        var frames = new List<(double DisplayTick, bool Attached, int Gestures, int ClaimedBy)>();
        var drawn = new DrawnFrame();
        AddChild(drawn);
        drawn.Drawn += () => frames.Add((Host.Context.NetworkObjectServer.Diagnostics.GetDisplayTick(2) ?? -1,
            crates[0].AttachedTo is not null, carriers[0].Gestures, crates[0].ClaimedBy));

        var attachTick = Client.Context.NetworkTime.Tick + 1;   // the state of this tick goes out as the next
        carriers[1].Gestures++;
        Expect.True(carriers[1].TryAttach(crates[1], hands[1]));
        Expect.True(await WaitUntil(() => crates[0].AttachedTo is not null, 3), "the host never showed the crate attached");
        for (var i = 0; i < 20; i++) await NextFrame();

        var detachTick = Client.Context.NetworkTime.Tick + 1;
        carriers[1].Gestures++;
        Expect.True(carriers[1].Detach(crates[1]));
        Expect.True(await WaitUntil(() => crates[0].AttachedTo is null, 3), "the host never showed the crate detached");
        for (var i = 0; i < 5; i++) await NextFrame();
        drawn.QueueFree();

        var claimed = frames.FindIndex(frame => frame.ClaimedBy == 2);
        var attached = frames.FindIndex(frame => frame.Attached);
        var grabbed = frames.FindIndex(frame => frame.Gestures == 1);
        var detached = frames.FindIndex(attached, frame => !frame.Attached);
        var thrown = frames.FindIndex(frame => frame.Gestures == 2);
        var report = $"claim record at frame {claimed}, attached at {attached} (display tick {frames[attached].DisplayTick:F1}, attach tick {attachTick}), "
                     + $"grab gesture at {grabbed}; detached at {detached} (display tick {frames[detached].DisplayTick:F1}, detach tick {detachTick}), throw gesture at {thrown}";
        GD.Print("ATTACH TIMING " + report);
        Expect.True(claimed >= 0 && claimed < attached, "the record did not come before the hand reached the crate, so this measures nothing: " + report);
        Expect.True(frames[attached].DisplayTick >= attachTick - 1, "shown in the hand before the carrier's playback got there: " + report);
        Expect.True(frames[attached - 1].DisplayTick <= attachTick + 1, "shown in the hand late: " + report);
        Expect.Equal(grabbed, attached, "the grab gesture and the attachment are not shown in the same frame: " + report);
        Expect.True(frames[detached].DisplayTick >= detachTick - 1, "shown free before the carrier's playback let go: " + report);
        Expect.Equal(thrown, detached, "the throw gesture and the detachment are not shown in the same frame: " + report);
    }

    /// <summary>
    /// The playground's failure: a player walks into a pile with a crate held out in front. The held crate is inside
    /// the pile before the player's body touches it; that touch takes the pile onto this peer, and a held crate that
    /// collides is then a static body inside two dynamic ones, which the engine throws out of the world. While
    /// attached the crate passes through them, and once let go it collides again: dropped, it rests on the floor.
    /// </summary>
    [Test]
    public async Task AHeldItemPassesThroughWhatItIsCarriedIntoAndCollidesAgainWhenLetGo()
    {
        var stacks = new[] { Host, Client };
        var carried = stacks.Select(stack => HarnessWorld.Crate(stack, "Carried", new Vector3(3, 0.5f, 0))).ToArray();
        var pile = stacks.Select(stack => new[]
        {
            HarnessWorld.Crate(stack, "Bottom", new Vector3(0, 0.5f, -5)),
            HarnessWorld.Crate(stack, "Top", new Vector3(0, 1.5f, -5)),
        }).ToArray();
        // The hand is a metre ahead, towards -Z, at the height of the top crate
        var carriers = stacks.Select(stack => HarnessWorld.Walker(stack, 2, new Vector3(0, 1, 0), Vector3.Zero)).ToArray();
        var hands = carriers.Select(carrier =>
        {
            var hand = new Marker3D { Name = "Hand", Position = new Vector3(0, 0.5f, -1) };
            carrier.AddChild(hand);
            return hand;
        }).ToArray();
        for (var i = 0; i < 20; i++) await NextFrame();

        Expect.True(carriers[1].TryAttach(carried[1], hands[1]));
        carriers[1].Walk = new Vector3(0, 0, -2);
        Expect.True(await WaitUntil(() => pile[1].All(crate => crate.Authority.IsLocal), 6),
            $"the carrier never took the pile by walking into it: at {carriers[1].GlobalPosition}, pile with {pile[1][0].Authority.Peer}");
        var inside = carried[1].GlobalPosition.DistanceTo(pile[1][1].GlobalPosition);
        carriers[1].Walk = Vector3.Zero;
        for (var seconds = 0.0; seconds < 1.5; seconds += GetProcessDeltaTime()) await NextFrame();

        var moved = pile.SelectMany(crates => crates).Select(crate => (crate.Name, Distance: crate.GlobalPosition.DistanceTo(new Vector3(0, crate.Name == "Top" ? 1.5f : 0.5f, -5)))).ToArray();
        var report = $"held crate {inside:F2} m from the top crate when the pile was taken; " + string.Join(", ", moved.Select(entry => $"{entry.Name} moved {entry.Distance:F2} m"));
        GD.Print("CARRIED INTO A PILE " + report);
        Expect.True(inside < 0.9f, "the held crate was not inside the pile when it was taken, so this measures nothing: " + report);
        Expect.True(moved.All(entry => entry.Distance < 0.3f), "the held crate shoved the pile it was carried into: " + report);

        Expect.True(carriers[1].Detach(carried[1]));
        Expect.True(await WaitUntil(() => carried[1].Sleeping || carried[1].LinearVelocity.Length() < 0.05f && carried[1].GlobalPosition.Y < 2, 5),
            $"the dropped crate never came to rest: {carried[1].GlobalPosition} at {carried[1].LinearVelocity}");
        Expect.True(carried[1].GlobalPosition.Y > 0.4f, $"the dropped crate fell through the floor: {carried[1].GlobalPosition}");
    }

    [Test]
    public async Task AnAnchorOutsideTheCarrierIsRefused()
    {
        var crate = HarnessWorld.Crate(Client, "Crate", new Vector3(0, 0.5f, 3));
        var carrier = HarnessWorld.Walker(Client, 2, new Vector3(0, 1, 0), Vector3.Zero);
        var elsewhere = new Marker3D { Name = "Elsewhere" };
        HarnessWorld.World(Client).AddChild(elsewhere);
        await NextFrame();

        Expect.False(carrier.TryAttach(crate, elsewhere), "an anchor outside the carrier was accepted");
        Expect.Null(crate.AttachedTo);
        Expect.Equal(0, crate.ClaimedBy, "the crate was claimed although the attach was refused");
    }

    [Test]
    public async Task ACycleIsRefused()
    {
        var first = HarnessWorld.Crate(Client, "First", new Vector3(0, 0.5f, 3));
        var second = HarnessWorld.Crate(Client, "Second", new Vector3(0, 0.5f, 5));
        var onFirst = new Marker3D { Name = "Top", Position = Vector3.Up };
        first.AddChild(onFirst);
        var onSecond = new Marker3D { Name = "Top", Position = Vector3.Up };
        second.AddChild(onSecond);
        await NextFrame();

        Expect.True(first.TryAttach(second, onFirst), "a crate cannot carry a crate");
        Expect.False(second.TryAttach(first, onSecond), "the carried crate took its own carrier");
        Expect.Equal(first, second.AttachedTo);
        Expect.Null(first.AttachedTo);
    }

    /// <summary>What hangs where, on the carrier's peer and on the host, with the hook telling both nodes each time.</summary>
    [Test]
    public async Task AttachedAndAttachedToFollowTheChangeOnEveryPeer()
    {
        var crates = new[] { HarnessWorld.Crate(Host, "Crate", new Vector3(0, 0.5f, 3)), HarnessWorld.Crate(Client, "Crate", new Vector3(0, 0.5f, 3)) };
        var carriers = new[] { HarnessWorld.Walker(Host, 2, new Vector3(0, 1, 0), Vector3.Zero), HarnessWorld.Walker(Client, 2, new Vector3(0, 1, 0), Vector3.Zero) };
        var hands = carriers.Select(HarnessWorld.BobbingHand).ToArray();
        for (var i = 0; i < 10; i++) await NextFrame();

        Expect.False(carriers[1].Detach(crates[1]), "detached what was never attached");
        Expect.True(carriers[1].TryAttach(crates[1], hands[1]));
        Expect.SequenceEqual([crates[1]], carriers[1].Attached);
        Expect.Equal(carriers[1], crates[1].AttachedTo);
        Expect.Equal(1, carriers[1].AttachmentChanges);
        Expect.Equal(0u, crates[1].CollisionLayer, "a held crate still collides");
        Expect.True(await WaitUntil(() => crates[0].AttachedTo == carriers[0], 3), "the host never showed it attached");
        Expect.SequenceEqual([crates[0]], carriers[0].Attached);
        Expect.Equal(1, carriers[0].AttachmentChanges);

        Expect.True(carriers[1].Detach(crates[1]));
        Expect.True(!carriers[1].Attached.Any() && crates[1].AttachedTo is null, "still attached after Detach");
        Expect.Equal(2, carriers[1].AttachmentChanges);
        Expect.Equal(1u, crates[1].CollisionLayer, "the crate's collisions did not come back");
        Expect.Equal(0, crates[1].ClaimedBy, "Detach kept the claim");
        Expect.True(await WaitUntil(() => crates[0].AttachedTo is null, 3), "the host never showed it detached");
        Expect.Equal(2, carriers[0].AttachmentChanges);
    }

    /// <summary>A peer joining while the crate is in the hand puts it there too, from the heartbeat that names the carrier.</summary>
    [Test]
    public async Task ALateJoinerSeesTheItemInTheHand()
    {
        var crates = new[] { HarnessWorld.Crate(Host, "Crate", new Vector3(0, 0.5f, 3)), HarnessWorld.Crate(Client, "Crate", new Vector3(0, 0.5f, 3)) };
        var carriers = new[] { HarnessWorld.Walker(Host, 2, new Vector3(0, 1, 0), Vector3.Zero), HarnessWorld.Walker(Client, 2, new Vector3(0, 1, 0), Vector3.Zero) };
        var hands = carriers.Select(HarnessWorld.BobbingHand).ToArray();
        for (var i = 0; i < 10; i++) await NextFrame();
        Expect.True(carriers[1].TryAttach(crates[1], hands[1]));
        Expect.True(await WaitUntil(() => crates[0].AttachedTo is not null, 3), "the host never showed it attached");

        var late = AddPeer(4);
        for (var i = 0; i < 15; i++) await NextFrame();
        var lateCrate = HarnessWorld.Crate(late, "Crate", new Vector3(0, 0.5f, 3));
        var lateCarrier = HarnessWorld.Walker(late, 2, new Vector3(0, 1, 0), Vector3.Zero);
        var lateHand = HarnessWorld.BobbingHand(lateCarrier);
        Expect.True(await WaitUntil(() => lateCrate.AttachedTo == lateCarrier, 5),
            $"the late joiner never put the crate in the hand: attached to {lateCrate.AttachedTo?.Name ?? "nothing"}, claimed by {lateCrate.ClaimedBy}");

        var worst = 0f;
        var drawn = new DrawnFrame();
        AddChild(drawn);
        drawn.Drawn += () => worst = Mathf.Max(worst, lateCrate.GlobalPosition.DistanceTo(lateHand.GlobalPosition));
        for (var i = 0; i < 20; i++) await NextFrame();
        drawn.QueueFree();
        Expect.True(worst < CarryTolerance, $"the late joiner draws the crate {worst:F3} m from the hand");
    }
}
