// Bodies built in code: CRN006 has nothing to check here
#pragma warning disable CRN006

using Godot;

namespace CrackNet.Tests;

/// <summary>
/// Animation over the network is parameters: a game marks AnimationTree inputs [Synced] and every peer runs its own
/// animation from them. What breaks is timing and double application, and each case here is one of those traps. See
/// docs/design/distributed-authority.md, "Carrying and animation".
/// </summary>
public partial class AnimationTests : HarnessSuite
{
    public override async Task BeforeCase()
    {
        await base.BeforeCase();
        Expect.True(await WaitUntil(() => Client.Context.NetworkTime.IsInitialSyncDone(), 5), "client never synced");
    }

    private const float ExactTolerance = 0.005f;

    /// <summary>A walk blend synced as a float changes on the observer in the frame the body starts moving: legs and body from the same sample.</summary>
    [Test]
    public async Task ALoopParameterChangesInTheFrameTheMotionDoes()
    {
        Network.LatencyMs = 80;
        var walkers = new[] { HarnessWorld.Walker(Host, 2, new Vector3(0, 1, 0), Vector3.Zero), HarnessWorld.Walker(Client, 2, new Vector3(0, 1, 0), Vector3.Zero) };
        // At rest on both peers first: landing, the walker slides a little, and the observer plays that back late
        var restX = float.NaN;
        Expect.True(await WaitUntil(() =>
        {
            var still = Mathf.Abs(walkers[1].GlobalPosition.X - restX) < 0.001f && Mathf.Abs(walkers[0].GlobalPosition.X - restX) < 0.001f;
            restX = walkers[1].GlobalPosition.X;
            return still;
        }, 5), "the walker never came to rest on both peers");
        for (var i = 0; i < 20; i++) await NextFrame();

        var frames = new List<(float X, float Blend)>();
        var drawn = new DrawnFrame();
        AddChild(drawn);
        drawn.Drawn += () => frames.Add((walkers[0].GlobalPosition.X, walkers[0].WalkBlend));
        walkers[1].Walk = new Vector3(2, 0, 0);
        for (var seconds = 0.0; seconds < 1.5; seconds += GetProcessDeltaTime()) await NextFrame();
        drawn.QueueFree();

        // The walker slides some 0.13 m after landing and the observer plays that back late too, so the walk is
        // found from the blend: the body has to stand still in the frames before it and move from that frame on
        var blended = frames.FindIndex(frame => frame.Blend > 0.001f);
        Expect.True(blended > 6, $"the walk blend never arrived, or too soon to judge: frame {blended} of {frames.Count}");
        var plateau = frames[blended - 1].X;
        var stillBefore = frames.Skip(blended - 6).Take(5).All(frame => Mathf.Abs(frame.X - plateau) < 0.0001f);   // a held sample repeats exactly
        // Both come from the same sample pair: in the frame the blend first reads 0.06, the body has moved 0.06 of a step
        var moved = frames.FindIndex(blended - 6, frame => Mathf.Abs(frame.X - plateau) > 0.0001f);
        var report = $"the walk blend from frame {blended}, the body from frame {moved}, still before it: {stillBefore}; around them: "
                     + string.Join(" ", frames.Skip(blended - 3).Take(6).Select(frame => $"({frame.X:F3}, {frame.Blend:F2})"));
        GD.Print("LOOP PARAMETER " + report);
        Expect.True(stillBefore, "the body was already moving before the blend arrived: " + report);
        Expect.Equal(blended, moved, "the animation and the motion start in different frames: " + report);
    }

    /// <summary>
    /// The one-shot pattern: a counter bumped in the tick of the action, and every peer plays the difference it
    /// receives. Once per bump; two bumps inside one snapshot arrive as one step of two, which a peer playing "on
    /// change" would play once; and the first value a late joiner receives is history, played zero times.
    /// </summary>
    [Test]
    public async Task AOneShotCounterPlaysOncePerBumpAndNotOnALateJoiner()
    {
        var walkers = new[] { HarnessWorld.Walker(Host, 2, new Vector3(0, 1, 0), Vector3.Zero), HarnessWorld.Walker(Client, 2, new Vector3(0, 1, 0), Vector3.Zero) };
        for (var i = 0; i < 10; i++) await NextFrame();
        var seen = new List<int>();
        var drawn = new DrawnFrame();
        AddChild(drawn);
        drawn.Drawn += () =>
        {
            if (seen.Count == 0 || seen[^1] != walkers[0].Gestures) seen.Add(walkers[0].Gestures);
        };

        walkers[1].Gestures++;
        Expect.True(await WaitUntil(() => walkers[0].Gestures == 1, 3), "the bump never arrived");
        Expect.Equal(1, walkers[0].GesturesPlayed, "one bump did not play once");
        for (var i = 0; i < 10; i++) await NextFrame();

        walkers[1].Gestures++;
        walkers[1].Gestures++;
        Expect.True(await WaitUntil(() => walkers[0].Gestures == 3, 3), "the double bump never arrived");
        Expect.SequenceEqual([0, 1, 3], seen, "the two bumps in one tick did not arrive as one step");
        Expect.Equal(3, walkers[0].GesturesPlayed, "two bumps in one step did not play twice");

        var late = AddPeer(4);
        for (var i = 0; i < 15; i++) await NextFrame();
        var lateWalker = HarnessWorld.Walker(late, 2, new Vector3(0, 1, 0), Vector3.Zero);
        Expect.True(await WaitUntil(() => lateWalker.Gestures == 3, 5), $"the late joiner never got the count: {lateWalker.Gestures}");
        for (var i = 0; i < 10; i++) await NextFrame();
        Expect.Equal(0, lateWalker.GesturesPlayed, "the late joiner played history as gestures");
        drawn.QueueFree();
    }

    /// <summary>
    /// The hand is on a bone that a LookAtModifier3D aims at a moving target: IK, applied by the skeleton in its own
    /// pass. The item in that hand is placed after it, on the authority's peer and on the host.
    /// </summary>
    [Test]
    public async Task AnItemInAHandMovedByIKStaysInIt()
    {
        var stacks = new[] { Host, Client };
        var crates = stacks.Select(stack => HarnessWorld.Crate(stack, "Crate", new Vector3(0, 0.5f, 3))).ToArray();
        var carriers = stacks.Select(stack => HarnessWorld.Walker(stack, 2, new Vector3(0, 1, 0), Vector3.Zero)).ToArray();
        var hands = carriers.Select(HarnessWorld.AimingHand).ToArray();
        for (var i = 0; i < 20; i++) await NextFrame();

        Expect.True(carriers[1].TryAttach(crates[1], hands[1]));
        Expect.True(await WaitUntil(() => crates[0].AttachedTo is not null, 3), "the host never showed it attached");
        for (var i = 0; i < 10; i++) await NextFrame();

        var worst = new float[stacks.Length];
        var low = new Vector3[stacks.Length];
        var high = new Vector3[stacks.Length];
        Array.Fill(low, Vector3.One * float.MaxValue);
        Array.Fill(high, Vector3.One * float.MinValue);
        var drawn = new DrawnFrame();
        AddChild(drawn);
        drawn.Drawn += () =>
        {
            for (var i = 0; i < stacks.Length; i++)
            {
                worst[i] = Mathf.Max(worst[i], crates[i].GlobalPosition.DistanceTo(hands[i].GlobalPosition));
                low[i] = low[i].Min(hands[i].GlobalPosition);
                high[i] = high[i].Max(hands[i].GlobalPosition);
            }
        };
        for (var seconds = 0.0; seconds < 2; seconds += GetProcessDeltaTime()) await NextFrame();
        drawn.QueueFree();

        var report = string.Join("; ", stacks.Select((stack, i) => $"{stack.Name}: crate off the hand by {worst[i]:F3} m, hand swept {(high[i] - low[i]).Length():F2} m"));
        GD.Print("IK HAND " + report);
        for (var i = 0; i < stacks.Length; i++)
            Expect.True((high[i] - low[i]).Length() > 0.5f, $"{stacks[i].Name}'s IK did not move the hand: " + report);
        Expect.True(worst.All(distance => distance < ExactTolerance), "the crate lags the IK hand: " + report);
    }

    /// <summary>
    /// Root motion runs on the authority only: the animation's displacement moves the body there and travels as the
    /// transform, and the observer's copy, whose own animation plays the same stride, is where the samples say.
    /// (An observer applying root motion too does not show on screen: playback rewrites the copy's transform every
    /// frame. It still walks a kinematic copy into the world between frames, so the pattern stays.)
    /// </summary>
    [Test]
    public async Task RootMotionOnTheAuthorityTravelsAsTheTransform()
    {
        var walkers = new[] { HarnessWorld.RootMotionWalker(Host, 2, "Strider", new Vector3(0, 1, 0)), HarnessWorld.RootMotionWalker(Client, 2, "Strider", new Vector3(0, 1, 0)) };
        for (var i = 0; i < 20; i++) await NextFrame();

        var sent = new SortedList<int, Vector3>();
        walkers[1].Net().Diagnostics.SampleSent += tick => sent[tick] = walkers[1].GlobalPosition;
        var shown = new List<(double Tick, Vector3 At)>();
        var drawn = new DrawnFrame();
        AddChild(drawn);
        drawn.Drawn += () =>
        {
            if (walkers[0].Net().Diagnostics.DisplayTick is { } tick) shown.Add((tick, walkers[0].GlobalPosition));
        };
        var startX = walkers[1].GlobalPosition.X;
        for (var seconds = 0.0; seconds < 3; seconds += GetProcessDeltaTime()) await NextFrame();
        drawn.QueueFree();

        var (error, compared) = Compare(sent, shown, Client.Context.NetworkObjectServer.StateIntervalTicks);
        var report = $"the authority walked {walkers[1].GlobalPosition.X - startX:F1} m on root motion; the host's copy is off the samples by {error:F3} m over {compared} frames";
        GD.Print("ROOT MOTION " + report);
        Expect.True(walkers[1].GlobalPosition.X - startX > 2, "root motion did not move the authority: " + report);
        Expect.True(compared > 15 && error < 0.1f, "the copy strays from the samples: " + report);
    }

    /// <summary>The worst distance between what was drawn at a display tick and the line between the samples sent around it.</summary>
    private static (float Worst, int Compared) Compare(SortedList<int, Vector3> sent, List<(double Tick, Vector3 At)> shown, int interval)
    {
        var worst = 0f;
        var compared = 0;
        var ticks = sent.Keys.ToList();
        foreach (var (tick, at) in shown)
        {
            var next = ticks.FindIndex(t => t >= tick);
            if (next <= 0 || ticks[next] - ticks[next - 1] > interval * 2) continue;
            var expected = sent.Values[next - 1].Lerp(sent.Values[next], (float)((tick - ticks[next - 1]) / (ticks[next] - ticks[next - 1])));
            worst = Mathf.Max(worst, expected.DistanceTo(at));
            compared++;
        }
        return (worst, compared);
    }
}
