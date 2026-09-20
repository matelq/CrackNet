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
        var stillFrames = 0;
        Expect.True(await WaitUntil(() =>
        {
            var still = Mathf.Abs(walkers[1].GlobalPosition.X - restX) < 0.0001f && Mathf.Abs(walkers[0].GlobalPosition.X - restX) < 0.0001f;
            stillFrames = still ? stillFrames + 1 : 0;
            restX = walkers[1].GlobalPosition.X;
            return stillFrames >= 30;
        }, 8), "the walker never came to rest on both peers");

        var frames = new List<(float X, float Blend)>();
        var drawn = new DrawnFrame();
        AddChild(drawn);
        drawn.Drawn += () => frames.Add((walkers[0].GlobalPosition.X, walkers[0].WalkBlend));
        walkers[1].Walk = new Vector3(2, 0, 0);
        for (var seconds = 0.0; seconds < 1.5; seconds += GetProcessDeltaTime()) await NextFrame();
        drawn.QueueFree();

        // The walker slides some 0.13 m after landing and the observer plays that back late too, so the walk is
        // found from the blend: the body has to stand still in the frames before it and move from that frame on
        var arrived = frames.FindIndex(frame => frame.Blend > 0.001f);
        Expect.True(arrived > 6 && arrived + 2 < frames.Count, $"the walk blend never arrived, or too soon to judge: frame {arrived} of {frames.Count}");
        var plateau = frames[arrived - 1].X;
        var stillBefore = frames.Skip(arrived - 6).Take(5).All(frame => Mathf.Abs(frame.X - plateau) < 0.0001f);   // a held sample repeats exactly

        // Two questions, because neither answers it alone.
        //
        // First, when each leaves rest. The thresholds cannot be the same number: over one sample pair the blend
        // covers its whole range from 0 to 1 while the body covers the few millimetres it managed in that tick,
        // accelerating from rest, so an epsilon on each trips at a different fraction of the same pair. On CI the
        // blend passed 0.001 a frame before the body passed 0.0001 m, which is that race and not a gap. A frame of
        // slack covers it and still fails on a real one: a display tick is two frames wide at 60 Hz against 30.
        var startedBlend = frames.FindIndex(frame => frame.Blend > 0.001f);
        var startedBody = frames.FindIndex(arrived - 6, frame => Mathf.Abs(frame.X - plateau) > 0.0001f);

        // Second, whether they then climb together, which the slack above would let through. Each is asked when it
        // is halfway through what it covers by two frames in - the same question of both, on their own scales.
        var blendHalf = frames[arrived + 2].Blend / 2;
        var moveHalf = Mathf.Abs(frames[arrived + 2].X - plateau) / 2;
        var blended = frames.FindIndex(frame => frame.Blend > blendHalf);
        var moved = frames.FindIndex(arrived - 6, frame => Mathf.Abs(frame.X - plateau) > moveHalf);

        var report = $"the walk blend leaves rest at frame {startedBlend} and is halfway at {blended}, the body at "
                     + $"{startedBody} and {moved}, still before it: {stillBefore}; "
                     + $"halves: blend {blendHalf:F3}, motion {moveHalf:F4} m; around them: "
                     + string.Join(" ", frames.Skip(arrived - 3).Take(6).Select(frame => $"({frame.X:F4}, {frame.Blend:F3})"));
        GD.Print("LOOP PARAMETER " + report);
        Expect.True(stillBefore, "the body was already moving before the blend arrived: " + report);
        Expect.True(Math.Abs(startedBlend - startedBody) <= 1, "the animation and the motion leave rest more than a frame apart: " + report);
        Expect.Equal(blended, moved, "the animation and the motion do not advance in the same frame: " + report);
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

    /// <summary>
    /// A state machine's state is an object, not a parameter: what travels is the state's index and the setter
    /// travels to it. Every observer is then in the state the samples say at the tick it is displaying, the same
    /// test the transform gets, and a state that is not sent leaves the observer where it started.
    /// </summary>
    [Test]
    public async Task AStateMachineTravelsToTheSameStateAtTheSameDisplayTick()
    {
        Network.LatencyMs = 80;
        var walkers = new[]
        {
            HarnessWorld.StateMachineWalker(Host, 2, "Stater", new Vector3(0, 1, 0)),
            HarnessWorld.StateMachineWalker(Client, 2, "Stater", new Vector3(0, 1, 0)),
        };
        // The first sample carries the state the walker starts in; recording before it lands would see the state
        // machine's own Start node, which is no one's state
        Expect.True(await WaitUntil(() => walkers[0].Playback!.GetCurrentNode() == Walker.AnimationStates[0], 5), "the observer never got the starting state");
        for (var i = 0; i < 20; i++) await NextFrame();

        var sent = new SortedList<int, int>();
        walkers[1].Net().Diagnostics.SampleSent += tick => sent[tick] = walkers[1].AnimationState;
        var shown = new List<(double Tick, string State)>();
        var drawn = new DrawnFrame();
        AddChild(drawn);
        drawn.Drawn += () =>
        {
            if (walkers[0].Net().Diagnostics.DisplayTick is { } tick) shown.Add((tick, walkers[0].Playback!.GetCurrentNode()));
        };

        // Walk the authority through the states, each held long enough to cover several samples
        var travelled = new List<int>();
        foreach (var state in new[] { 1, 2, 0, 1 })
        {
            walkers[1].AnimationState = state;
            travelled.Add(state);
            for (var seconds = 0.0; seconds < 0.5; seconds += GetProcessDeltaTime()) await NextFrame();
        }
        drawn.QueueFree();

        // The observer's own transitions: the first frame it shows each new state, and the tick it was displaying
        var entered = new List<(string State, double Tick)>();
        foreach (var (tick, state) in shown)
            if (entered.Count == 0 || entered[^1].State != state)
                entered.Add((state, tick));

        // The authority's: the tick stamped on the sample that first carried each new state
        var changes = new List<(string State, int Tick)>();
        foreach (var (tick, state) in sent)
            if (changes.Count == 0 || changes[^1].State != Walker.AnimationStates[state])
                changes.Add((Walker.AnimationStates[state], tick));

        var expected = travelled.Select(state => Walker.AnimationStates[state]).Prepend("idle").ToList();
        var late = entered.Skip(1).Zip(changes).Select(pair => pair.First.Tick - pair.Second.Tick).ToList();
        var report = $"the observer entered {string.Join("/", entered.Select(e => $"{e.State}@{e.Tick:F1}"))}, "
                     + $"sent {string.Join("/", changes.Select(c => $"{c.State}@{c.Tick}"))} over {shown.Count} frames";
        GD.Print("STATE MACHINE " + report);
        Expect.True(walkers[1].Playback!.GetCurrentNode() == Walker.AnimationStates[travelled[^1]], "the authority itself did not travel: " + report);
        Expect.SequenceEqual(expected, entered.Select(e => e.State).ToList(), "the observer did not follow the authority through the states: " + report);
        Expect.SequenceEqual(expected.Skip(1).ToList(), changes.Select(c => c.State).ToList(), "the states did not go out one sample each: " + report);
        // Each state is entered at the tick the sample carrying it is stamped with, not when the packet arrived: a
        // frame is a tick or two of display time wide, and 80 ms of latency is five ticks
        Expect.True(late.Count == changes.Count && late.All(delta => Math.Abs(delta) <= 2),
            "the observer entered a state at a different tick than the sample says: " + string.Join(", ", late.Select(d => $"{d:F1}")) + "; " + report);
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
