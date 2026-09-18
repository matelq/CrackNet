// The harness builds its bodies in code, to vary mass, shapes and push strength per case rather than keep a scene for
// each: CRN006, which asks for a scene with a NetworkObject, has nothing to check here
#pragma warning disable CRN006

using Godot;

namespace CrackNet.Tests;

/// <summary>
/// What NetworkObject does for a physics root without game code: freezing where another peer simulates it, passing
/// authority on contact, handing a settled body back, knocks and throws. Each stack gets its own physics world, or
/// the host's crate and the client's copy of it would collide with each other.
/// </summary>
public partial class PhysicsObjectTests : HarnessSuite
{
    public override async Task BeforeCase()
    {
        await base.BeforeCase();
        Expect.True(await WaitUntil(() => Client.Context.NetworkTime.IsInitialSyncDone(), 5), "client never synced");
    }

    private static Node World(CrackNetStack stack)
    {
        if (stack.GetNodeOrNull("World") is { } world) return world;
        var viewport = new SubViewport { Name = "World", OwnWorld3D = true, Size = new Vector2I(2, 2) };
        stack.AddChild(viewport);
        var floor = new StaticBody3D { Name = "Floor", Position = new Vector3(0, -0.5f, 0) };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(100, 1, 100) } });
        viewport.AddChild(floor);
        return viewport;
    }

    private static RigidBody3D Crate(CrackNetStack stack, string name, Vector3 position)
    {
        var crate = new RigidBody3D { Name = name, Position = position };
        crate.SetMultiplayerAuthority(1);
        crate.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One } });
        crate.AddChild(new NetworkObject { Name = "NetworkObject" });
        World(stack).AddChild(crate);
        return crate;
    }

    private static Walker Walker(CrackNetStack stack, int peer, Vector3 position, Vector3 velocity, float pushStrength = 0)
    {
        var walker = new Walker { Name = $"Walker{peer}", Position = position, Walk = velocity };
        walker.SetMultiplayerAuthority(peer);
        walker.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.4f, Height = 1.8f } });
        walker.AddChild(new NetworkObject { Name = "NetworkObject", ImpulseStrength = pushStrength });
        World(stack).AddChild(walker);
        return walker;
    }

    private static NetworkObject Net(Node root) => root.GetNode<NetworkObject>("NetworkObject");

    [Test]
    public async Task ACrateIsFrozenWhereAnotherPeerSimulatesIt()
    {
        var onHost = Crate(Host, "Crate", new Vector3(0, 0.5f, 0));
        var onClient = Crate(Client, "Crate", new Vector3(0, 0.5f, 0));
        await NextFrame();

        Expect.Equal(NetworkObject.ObjectKind.Shared, Net(onHost).ResolvedKind);
        Expect.False(onHost.Freeze, "the host simulates its own crate");
        Expect.True(onClient.Freeze, "the client plays the host's crate back");
    }

    [Test]
    public async Task WalkingIntoACrateTakesItAndItGoesBackAtRest()
    {
        var crates = new[] { Crate(Host, "Crate", new Vector3(0, 0.5f, 0)), Crate(Client, "Crate", new Vector3(0, 0.5f, 0)) };
        Walker(Host, 2, new Vector3(-3, 1, 0), Vector3.Zero);
        Walker(Client, 2, new Vector3(-3, 1, 0), new Vector3(4, 0, 0));

        Expect.True(await WaitUntil(() => crates.All(crate => Net(crate).Authority.Peer == 2), 5),
            $"the walker never took the crate: {string.Join(", ", crates.Select(crate => Net(crate).Authority.Peer))}");
        Expect.False(crates[1].Freeze, "the client simulates the crate it took");
        Expect.True(crates[0].Freeze, "the host plays it back");

        // Stepped back from it: once it has settled, it goes back. Leaning on it, the player would keep it
        Client.GetNode<Walker>("World/Walker2").Walk = new Vector3(-4, 0, 0);
        for (var i = 0; i < 10; i++) await NextFrame();
        Client.GetNode<Walker>("World/Walker2").Walk = Vector3.Zero;
        Expect.True(await WaitUntil(() => crates.All(crate => Net(crate).Authority.Peer == 1), 8),
            $"the settled crate never went back to the host: {string.Join(", ", crates.Select(crate => Net(crate).Authority.Peer))}");
    }

    [Test]
    public async Task APushReachesTheAuthorityOfARigidBodyAndOfACharacter()
    {
        var crates = new[] { Crate(Host, "Crate", new Vector3(0, 0.5f, 0)), Crate(Client, "Crate", new Vector3(0, 0.5f, 0)) };
        var walkers = new[] { Walker(Host, 2, new Vector3(5, 1, 0), Vector3.Zero), Walker(Client, 2, new Vector3(5, 1, 0), Vector3.Zero) };
        var knocked = new List<(int Peer, Vector3 Impulse)>();
        foreach (var walker in walkers)
            Net(walker).Impulsed += impulse => knocked.Add((walker.Multiplayer.GetUniqueId(), impulse));
        await NextFrame();

        Net(crates[1]).Impulse(new Vector3(0, 0, 8));
        Net(walkers[0]).Impulse(new Vector3(3, 0, 0));

        Expect.True(await WaitUntil(() => crates[0].LinearVelocity.Z > 1 && knocked.Count > 0, 3),
            $"crate velocity {crates[0].LinearVelocity}, knocked {knocked.Count}");
        for (var i = 0; i < 10; i++) await NextFrame();
        Expect.SequenceEqual([(2, new Vector3(3, 0, 0))], knocked);
        Expect.Equal(new Vector3(3, 0, 0), walkers[1].Net().TakeImpulses(0), "the push waits in the knockback too");
    }

    [Test]
    public async Task AThrowFliesOnTheThrowersSimulation()
    {
        var crates = new[] { Crate(Host, "Crate", new Vector3(0, 0.5f, 0)), Crate(Client, "Crate", new Vector3(0, 0.5f, 0)) };
        await NextFrame();

        Expect.True(Net(crates[1]).TryClaim());
        Expect.True(crates[1].Freeze, "a held crate is moved by hand");
        Expect.True(Net(crates[1]).ReleaseClaim(new Vector3(6, 3, 0)));

        Expect.False(crates[1].Freeze, "a thrown crate is simulated by the thrower");
        Expect.True(crates[1].LinearVelocity.X > 5, $"thrown at {crates[1].LinearVelocity}");
        Expect.True(await WaitUntil(() => crates[0].GlobalPosition.X > 1, 3), $"the host never saw it fly: {crates[0].GlobalPosition}");
    }

    [Test]
    public async Task TakingTheBottomOfAStackTakesTheWholeStack()
    {
        // The crates above are taken the moment the bottom one is. Their requests name the bottom crate as the cause,
        // so the host has to hear about the bottom crate first, or it refuses them and the stack hangs in the air
        NetworkObject[] Stack(CrackNetStack stack) =>
        [
            Net(Crate(stack, "Bottom", new Vector3(0, 0.5f, 0))),
            Net(Crate(stack, "Middle", new Vector3(0, 1.5f, 0))),
            Net(Crate(stack, "Top", new Vector3(0, 2.5f, 0))),
        ];
        var onHost = Stack(Host);
        var onClient = Stack(Client);
        for (var i = 0; i < 30; i++) await NextFrame();

        // A settled stack goes back to the host half a second later, so what counts is that each crate went to the client
        var reachedClient = new HashSet<string>();
        foreach (var obj in onHost)
            obj.AuthorityChanged += () => { if (obj.Authority.Peer == 2) reachedClient.Add(obj.Root!.Name); };
        var rejected = new HashSet<string>();
        foreach (var obj in onClient)
            obj.AuthorityChanged += () => { if (obj.Authority.Peer == 1 && reachedClient.Count < 3) rejected.Add(obj.Root!.Name); };

        Expect.True(onClient[0].Authority.Take());
        await WaitUntil(() => reachedClient.Count == 3, 2);
        Expect.True(reachedClient.Count == 3 && rejected.Count == 0,
            $"reached the client on the host: {string.Join(", ", reachedClient)}; taken back from the client: {string.Join(", ", rejected)}");
    }

    [Test]
    public async Task StrikingACrateTakesItAndItFliesAtOnce()
    {
        // Slow enough that waiting for the host would show
        Network.LatencyMs = 150;
        var crates = new[] { Crate(Host, "Crate", new Vector3(0, 0.5f, 0)), Crate(Client, "Crate", new Vector3(0, 0.5f, 0)) };
        Walker(Host, 2, new Vector3(-3, 1, 0), Vector3.Zero);
        var striker = Walker(Client, 2, new Vector3(-3, 1, 0), Vector3.Zero);
        for (var i = 0; i < 5; i++) await NextFrame();

        striker.Impulse(crates[1], new Vector3(0, 0, 8));
        Expect.Equal(2, crates[1].Net().Authority.Peer, "the striker took the crate");
        // Physics steps, not rendered frames: a rendered frame may run none, and a body switching from frozen static to
        // dynamic takes the impulse on its second step
        for (var i = 0; i < 3; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        Expect.True(crates[1].LinearVelocity.Z > 1, $"the crate did not fly at once on the striker's peer: {crates[1].LinearVelocity}");
        Expect.True(await WaitUntil(() => crates[0].Net().Authority.Peer == 2 && crates[0].GlobalPosition.Z > 0.3f, 3),
            $"the host never saw it: authority {crates[0].Net().Authority.Peer}, at {crates[0].GlobalPosition}");
    }

    /// <summary>
    /// A strike's state goes straight to the other peers, its authority change the long way through the host. A
    /// third peer that dropped the samples arriving first saw the crate sit still, then jump into the middle of its
    /// flight. Measured on the samples kept rather than per frame: a headless frame covers anywhere from half a tick to three.
    /// </summary>
    [Test]
    public async Task AnObserverShowsAStruckCrateFromTheStrike()
    {
        Network.LatencyMs = 100;
        var third = AddPeer(3);
        Expect.True(await WaitUntil(() => third.Context.NetworkTime.IsInitialSyncDone(), 5), "third peer never synced");
        var stacks = new[] { Host, Client, third };
        var crates = stacks.Select(stack => Crate(stack, "Crate", new Vector3(0, 0.5f, 0))).ToArray();
        var strikers = stacks.Select(stack => Walker(stack, 2, new Vector3(-12, 1, 0), Vector3.Zero)).ToArray();
        for (var i = 0; i < 30; i++) await NextFrame();

        // What the observer kept of the striker's state, from the tick of the strike on
        var kept = new List<int>();
        crates[2].Net().Diagnostics.SampleReceived += tick =>
        {
            if (crates[2].Net().Authority.Peer == 2) kept.Add(tick);
        };
        var struckAt = Client.Context.NetworkTime.Tick;
        strikers[1].Impulse(crates[1], new Vector3(10, 0, 0));

        Expect.True(await WaitUntil(() => kept.Count > 10, 5), "the observer never played the striker's state");
        // The first sample after the strike is at most one send interval away
        Expect.True(kept.Min() <= struckAt + NetworkObjectServer.StateIntervalTicks,
            $"the observer's first sample of the flight is for tick {kept.Min()}, struck at {struckAt}: its opening was dropped");
    }

    [Test]
    public async Task TwoStrikesFromOppositeSidesAtOnceBothCount() => await StrikeFromBothSides(gapMs: 0);

    [Test]
    public async Task TwoStrikesCloserThanThePingBothCount() => await StrikeFromBothSides(gapMs: 60);

    /// <summary>
    /// Two guests strike the same crate from opposite sides. Each takes it for itself before the host has answered,
    /// and the host gives it to one of them: the loser's strike has to reach the winner, or the crate flies off as if
    /// only one had hit it. It arrives a round trip late, so the two do not cancel out: the crate flies one way, then
    /// the other, on the winner's simulation.
    /// </summary>
    private async Task StrikeFromBothSides(int gapMs)
    {
        Network.LatencyMs = 100;
        var third = AddPeer(3);
        Expect.True(await WaitUntil(() => third.Context.NetworkTime.IsInitialSyncDone(), 5), "third peer never synced");
        var stacks = new[] { Host, Client, third };
        var crates = stacks.Select(stack => Crate(stack, "Crate", new Vector3(0, 0.5f, 0))).ToArray();
        // Far enough that the crate never reaches them: it stops on friction alone
        var left = stacks.Select(stack => Walker(stack, 2, new Vector3(-12, 1, 0), Vector3.Zero)).ToArray();
        var right = stacks.Select(stack => Walker(stack, 3, new Vector3(12, 1, 0), Vector3.Zero)).ToArray();
        for (var i = 0; i < 10; i++) await NextFrame();

        left[1].Impulse(crates[1], new Vector3(10, 0, 0));
        if (gapMs > 0) await ToSignal(GetTree().CreateTimer(gapMs / 1000.0), SceneTreeTimer.SignalName.Timeout);
        right[2].Impulse(crates[2], new Vector3(-10, 0, 0));

        // On the winner's own simulation, whichever peer that is
        float fastest = 0, slowest = 0;
        for (var i = 0; i < 90; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            var winner = crates[crates[0].Net().Authority.Peer - 1];
            if (!winner.Net().Authority.IsLocal) continue;
            fastest = Mathf.Max(fastest, winner.LinearVelocity.X);
            slowest = Mathf.Min(slowest, winner.LinearVelocity.X);
        }
        Expect.True(fastest > 2 && slowest < -2,
            $"the winner, peer {crates[0].Net().Authority.Peer}, moved it between {slowest:F2} and {fastest:F2} m/s: one strike was lost");
    }

    [Test]
    public async Task StrikingAPlayerPushesItOnItsOwnPeer()
    {
        var pushed = new List<(int Peer, Vector3 Impulse)>();
        var targets = new[] { Walker(Host, 1, new Vector3(3, 1, 0), Vector3.Zero), Walker(Client, 1, new Vector3(3, 1, 0), Vector3.Zero) };
        foreach (var target in targets)
            target.Net().Impulsed += impulse => pushed.Add((target.Multiplayer.GetUniqueId(), impulse));
        Walker(Host, 2, new Vector3(-3, 1, 0), Vector3.Zero);
        var striker = Walker(Client, 2, new Vector3(-3, 1, 0), Vector3.Zero);
        for (var i = 0; i < 5; i++) await NextFrame();

        striker.Impulse(targets[1], new Vector3(4, 0, 0));
        Expect.True(await WaitUntil(() => pushed.Count > 0, 3), "never pushed");
        for (var i = 0; i < 10; i++) await NextFrame();
        Expect.SequenceEqual([(1, new Vector3(4, 0, 0))], pushed);
        Expect.Equal(1, targets[1].Net().Authority.Peer, "a player is never taken");
    }

    [Test]
    public async Task ImpulseStrengthPushesWhatTheCharacterWalksInto()
    {
        async Task<float> Travel(float strength, string name)
        {
            var crates = new[] { Crate(Host, name, new Vector3(0, 0.5f, 0)), Crate(Client, name, new Vector3(0, 0.5f, 0)) };
            var walkerName = $"Walker2";
            Walker(Host, 2, new Vector3(-2, 1, 0), Vector3.Zero, strength);
            Walker(Client, 2, new Vector3(-2, 1, 0), new Vector3(3, 0, 0), strength);
            for (var i = 0; i < 90; i++) await NextFrame();
            var travel = crates[1].GlobalPosition.X;
            foreach (var stack in new[] { Host, Client })
            {
                stack.GetNode($"World/{name}").Free();
                stack.GetNode($"World/{walkerName}").Free();
            }
            return travel;
        }

        var without = await Travel(0, "CrateA");
        var with = await Travel(2, "CrateB");
        Expect.True(with > without + 0.5f, $"with ImpulseStrength the crate moved to {with}, without to {without}");
    }

    [Test]
    public async Task ACrateRestingOnOneThisPeerStillSimulatesDoesNotGoBackAlone()
    {
        // The top crate settles while the bottom one is held. Handed back on its own, it would be the host's, frozen
        // here, and hang in the air for a network delay the moment the bottom one is lifted away
        NetworkObject[] Stack(CrackNetStack stack) =>
        [
            Net(Crate(stack, "Bottom", new Vector3(0, 0.5f, 0))),
            Net(Crate(stack, "Top", new Vector3(0, 1.5f, 0))),
        ];
        var onHost = Stack(Host);
        var onClient = Stack(Client);
        for (var i = 0; i < 30; i++) await NextFrame();

        Expect.True(onClient[0].TryClaim(), "the client could not claim the bottom crate");
        Expect.True(await WaitUntil(() => onHost[1].Authority.Peer == 2, 3), "the top crate never followed the bottom one");

        // Well past the half second of rest after which a crate goes back
        for (var i = 0; i < 120; i++) await NextFrame();
        Expect.Equal(2, onClient[1].Authority.Peer, "the top crate went back to the host while resting on a held one");
    }

    [Test]
    public async Task ATouchingGroupGoesBackToTheHostTogether()
    {
        // Each crate counted its own rest. The top one settled first and went back alone, leaving a stack simulated
        // half here and half on the host: the host's crate then bumped the client's one and took it, and the stack
        // hung on the client's screen
        NetworkObject[] Stack(CrackNetStack stack) =>
        [
            Net(Crate(stack, "Bottom", new Vector3(0, 0.5f, 0))),
            Net(Crate(stack, "Top", new Vector3(0, 1.5f, 0))),
        ];
        Stack(Host);
        var onClient = Stack(Client);
        for (var i = 0; i < 30; i++) await NextFrame();

        Expect.True(onClient[0].TryClaim(), "the client could not claim the bottom crate");
        Expect.True(await WaitUntil(() => onClient[1].Authority.Peer == 2, 3), "the top crate never followed");
        // The top crate rests part of the way to going back, the bottom one has not started counting
        for (var i = 0; i < 45; i++) await NextFrame();
        Expect.True(onClient[0].ReleaseClaim(), "could not release the bottom crate");

        var split = 0;
        for (var frame = 0; frame < 150 && onClient.Any(obj => obj.Authority.Peer == 2); frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            if (onClient[0].Authority.Peer != onClient[1].Authority.Peer) split++;
        }
        Expect.True(onClient.All(obj => obj.Authority.Peer == 1), "the group never went back");
        Expect.Equal(0, split, "physics frames in which the touching crates were simulated on different peers");
    }

    [Test]
    public async Task AGroupDoesNotGoBackWhileOneOfItsCratesMoves()
    {
        // Two crates side by side, both the client's: one settles, the other keeps sliding along it
        NetworkObject[] Pair(CrackNetStack stack) =>
        [
            Net(Crate(stack, "Still", new Vector3(0, 0.5f, 0))),
            Net(Crate(stack, "Sliding", new Vector3(1.02f, 0.5f, 0))),
        ];
        Pair(Host);
        var onClient = Pair(Client);
        for (var i = 0; i < 20; i++) await NextFrame();
        Expect.True(onClient[0].Authority.Take() && onClient[1].Authority.Take(), "the client could not take the crates");

        var sliding = (RigidBody3D)onClient[1].Root!;
        var returned = false;
        for (var frame = 0; frame < 90; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            sliding.LinearVelocity = new Vector3(0, 0, 0.5f);
            returned |= onClient[0].Authority.Peer == 1;
        }
        Expect.False(returned, "the still crate went back while the one touching it was moving");
    }

    [Test]
    public async Task StandingOnACrateDoesNotBounceIt()
    {
        // Standing on a crate used to take it: at rest it went back to the host and the next floor contact took it
        // again, every unfreeze let Rapier push the crate out of the character, and the crate threw the character up
        Network.LatencyMs = 150;
        var crates = new[] { Crate(Host, "Crate", new Vector3(0, 0.5f, 0)), Crate(Client, "Crate", new Vector3(0, 0.5f, 0)) };
        Walker(Host, 2, new Vector3(0, 2, 0), Vector3.Zero, pushStrength: 0.6f);
        var walker = Walker(Client, 2, new Vector3(0, 2, 0), Vector3.Zero, pushStrength: 0.6f);
        var changes = 0;
        crates[1].Net().AuthorityChanged += () => changes++;

        var crateHighest = 0f;
        var walkerHighest = 0f;
        for (var frame = 0; frame < 300; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            if (frame < 30) continue;   // landing on it
            crateHighest = Math.Max(crateHighest, Math.Max(crates[0].GlobalPosition.Y, crates[1].GlobalPosition.Y));
            walkerHighest = Math.Max(walkerHighest, walker.GlobalPosition.Y);
        }
        Expect.True(crateHighest < 0.6f && walkerHighest < 2.1f,
            $"the crate bounced up to {crateHighest}, the character to {walkerHighest}; authority changed {changes} times");
        Expect.True(changes <= 1, $"authority over the crate under a standing character changed {changes} times");
    }

    [Test]
    public async Task AnAnswerToAnOlderRequestDoesNotUndoAClaim()
    {
        // Smoke: walking into a crate, it went back to the host and was taken again, then grabbed; the host's answers
        // to the earlier requests arrived after the grab and let go of the crate for a moment, in the hand and inside
        // the crate above it, and the physics engine shot that one off
        Network.LatencyMs = 150;
        var crates = new[] { Crate(Host, "Crate", new Vector3(0, 0.5f, 0)), Crate(Client, "Crate", new Vector3(0, 0.5f, 0)) };
        await NextFrame();
        var onClient = Net(crates[1]);

        Expect.True(onClient.Authority.Take());
        Expect.True(onClient.Authority.ReturnToHost());
        Expect.True(onClient.Authority.Take());
        Expect.True(onClient.TryClaim());
        var dropped = new List<string>();
        onClient.AuthorityChanged += () => dropped.Add($"authority {onClient.Authority.Peer} holder {onClient.ClaimedBy}");

        await WaitUntil(() => onClient.PendingRequest == 0, 3);
        for (var i = 0; i < 20; i++) await NextFrame();
        Expect.True(onClient.ClaimedBy == 2 && dropped.Count == 0, $"the claim was undone on the claimer: {string.Join("; ", dropped)}");
    }

    [Test]
    public async Task ACrateStaysWithThePlayerStandingOnIt()
    {
        // Playtest: the crate a player stood on went back to the host after resting under them, and from then on each
        // peer had the other one a network delay behind: the bodies overlapped and the player was thrown up
        Network.LatencyMs = 150;
        var crates = new[] { Crate(Host, "Crate", new Vector3(0, 0.5f, 0)), Crate(Client, "Crate", new Vector3(0, 0.5f, 0)) };
        Walker(Host, 2, new Vector3(0, 1.95f, 0), Vector3.Zero);
        var walker = Walker(Client, 2, new Vector3(0, 1.95f, 0), Vector3.Zero);
        walker.SafeMargin = 0.05f;   // as in the playground: at Godot's default Rapier bounces the crate off on its own
        await NextFrame();
        Expect.True(Net(crates[1]).Authority.Take());
        var changes = new List<int>();
        Net(crates[1]).AuthorityChanged += () => changes.Add(Net(crates[1]).Authority.Peer);

        var walkerHighest = 0f;
        for (var frame = 0; frame < 240; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            walkerHighest = Math.Max(walkerHighest, walker.GlobalPosition.Y);
        }
        Expect.True(changes.Count == 0, $"the crate under a standing player changed authority: {string.Join(", ", changes)}");
        Expect.True(walkerHighest < 2.0f && walker.GlobalPosition.Y > 1.8f, $"the player was thrown up to {walkerHighest}, ended at {walker.GlobalPosition}");
    }

    [Test]
    public async Task AnotherPeersCrateDoesNotShoveMine()
    {
        // A copy of another peer's body is kinematic: it pushed this peer's bodies with infinite mass, which neither
        // mass nor Rapier's corrective velocity limits. On the host, the copy of a crate thrown from inside a stack
        // shot the stack off at 10-15 m/s
        var standing = new[] { Crate(Host, "Standing", new Vector3(2, 0.5f, 0)), Crate(Client, "Standing", new Vector3(2, 0.5f, 0)) };
        var moved = new[] { Crate(Host, "Moved", new Vector3(-2, 0.5f, 0)), Crate(Client, "Moved", new Vector3(-2, 0.5f, 0)) };
        for (var i = 0; i < 30; i++) await NextFrame();

        Expect.True(Net(moved[1]).TryClaim());
        for (var i = 0; i < 90; i++)
        {
            moved[1].GlobalPosition = new Vector3(-2 + 5 * Math.Min(1, i / 60f), 0.5f, 0);
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        for (var i = 0; i < 30; i++) await NextFrame();

        Expect.Equal(1, Net(standing[0]).Authority.Peer, "nothing took the standing crate");
        Expect.True(standing[0].GlobalPosition.DistanceTo(new Vector3(2, 0.5f, 0)) < 0.1f,
            $"the host's crate was shoved to {standing[0].GlobalPosition} by the copy of the client's");
    }

    [Test]
    public async Task AThrownCrateTakesAndKnocksWhatItHits()
    {
        // A crate simulated here passes through copies of other peers' crates, so it has to take one before reaching it
        Network.LatencyMs = 150;
        var target = new[] { Crate(Host, "Target", new Vector3(3, 0.5f, 0)), Crate(Client, "Target", new Vector3(3, 0.5f, 0)) };
        var thrown = new[] { Crate(Host, "Thrown", new Vector3(0, 0.5f, 0)), Crate(Client, "Thrown", new Vector3(0, 0.5f, 0)) };
        for (var i = 0; i < 30; i++) await NextFrame();

        Expect.True(Net(thrown[1]).TryClaim());
        await NextFrame();
        Expect.True(Net(thrown[1]).ReleaseClaim(new Vector3(8, 0, 0)));

        Expect.True(await WaitUntil(() => target[1].GlobalPosition.X > 3.2f, 2),
            $"the crate passed through or stopped: target at {target[1].GlobalPosition} authority {Net(target[1]).Authority.Peer}, thrown at {thrown[1].GlobalPosition}");
        Expect.True(thrown[1].GlobalPosition.X < target[1].GlobalPosition.X, $"the thrown crate went through: {thrown[1].GlobalPosition} vs {target[1].GlobalPosition}");
    }

    [Test]
    public async Task APlayerIsNotLaunchedByTheCrateUnderItJumping()
    {
        // Playtest: players flew 140 m up. A crate simulated elsewhere is kinematic here, and Rapier gives a kinematic
        // body the velocity of its last move: a copy that snaps (a correction, a freeze, a grab) moves a metre in a frame,
        // 60 m/s, and a character standing on it takes that as platform velocity and keeps it
        var crate = Crate(Client, "Crate", new Vector3(0, 0.5f, 0));
        var walker = Walker(Client, 2, new Vector3(0, 1.95f, 0), Vector3.Zero);
        walker.SafeMargin = 0.05f;
        walker.Falls = true;
        for (var i = 0; i < 40; i++) await NextFrame();
        Expect.True(crate.Freeze && walker.IsOnFloor(), $"the walker stands on a copy: frozen {crate.Freeze}, at {walker.GlobalPosition}");

        crate.GlobalPosition += new Vector3(0, 0.3f, 0);
        var highest = 0f;
        for (var i = 0; i < 60; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            highest = Math.Max(highest, walker.GlobalPosition.Y);
        }
        Expect.True(highest < 3, $"the walker was launched to {highest}, velocity {walker.Velocity}");
    }
}
