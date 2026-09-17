using Godot;

namespace Netfox.Tests;

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

    private static Node World(NetfoxStack stack)
    {
        if (stack.GetNodeOrNull("World") is { } world) return world;
        var viewport = new SubViewport { Name = "World", OwnWorld3D = true, Size = new Vector2I(2, 2) };
        stack.AddChild(viewport);
        var floor = new StaticBody3D { Name = "Floor", Position = new Vector3(0, -0.5f, 0) };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(100, 1, 100) } });
        viewport.AddChild(floor);
        return viewport;
    }

    private static RigidBody3D Crate(NetfoxStack stack, string name, Vector3 position)
    {
        var crate = new RigidBody3D { Name = name, Position = position };
        crate.SetMultiplayerAuthority(1);
        crate.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One } });
        crate.AddChild(new NetworkObject { Name = "NetworkObject" });
        World(stack).AddChild(crate);
        return crate;
    }

    private static Walker Walker(NetfoxStack stack, int peer, Vector3 position, Vector3 velocity, float pushStrength = 0)
    {
        var walker = new Walker { Name = $"Walker{peer}", Position = position, Walk = velocity };
        walker.SetMultiplayerAuthority(peer);
        walker.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.4f, Height = 1.8f } });
        walker.AddChild(new NetworkObject { Name = "NetworkObject", PushStrength = pushStrength });
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

        // Stopped walking into it: once it has settled, it goes back
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
            Net(walker).Pushed += impulse => knocked.Add((walker.Multiplayer.GetUniqueId(), impulse));
        await NextFrame();

        Net(crates[1]).Push(new Vector3(0, 0, 8));
        Net(walkers[0]).Push(new Vector3(3, 0, 0));

        Expect.True(await WaitUntil(() => crates[0].LinearVelocity.Z > 1 && knocked.Count > 0, 3),
            $"crate velocity {crates[0].LinearVelocity}, knocked {knocked.Count}");
        for (var i = 0; i < 10; i++) await NextFrame();
        Expect.SequenceEqual([(2, new Vector3(3, 0, 0))], knocked);
        Expect.Equal(new Vector3(3, 0, 0), walkers[1].Net().TakeKnockback(0), "the push waits in the knockback too");
    }

    [Test]
    public async Task AThrowFliesOnTheThrowersSimulation()
    {
        var crates = new[] { Crate(Host, "Crate", new Vector3(0, 0.5f, 0)), Crate(Client, "Crate", new Vector3(0, 0.5f, 0)) };
        await NextFrame();

        Expect.True(Net(crates[1]).TryClaim());
        Expect.True(crates[1].Freeze, "a held crate is moved by hand");
        Expect.True(Net(crates[1]).Throw(new Vector3(6, 3, 0)));

        Expect.False(crates[1].Freeze, "a thrown crate is simulated by the thrower");
        Expect.True(crates[1].LinearVelocity.X > 5, $"thrown at {crates[1].LinearVelocity}");
        Expect.True(await WaitUntil(() => crates[0].GlobalPosition.X > 1, 3), $"the host never saw it fly: {crates[0].GlobalPosition}");
    }

    [Test]
    public async Task TakingTheBottomOfAStackTakesTheWholeStack()
    {
        // The crates above are taken the moment the bottom one is. Their requests name the bottom crate as the cause,
        // so the host has to hear about the bottom crate first, or it refuses them and the stack hangs in the air
        NetworkObject[] Stack(NetfoxStack stack) =>
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

        striker.Push(crates[1], new Vector3(0, 0, 8));
        Expect.Equal(2, crates[1].Net().Authority.Peer, "the striker took the crate");
        for (var i = 0; i < 3; i++) await NextFrame();
        Expect.True(crates[1].LinearVelocity.Z > 1, $"the crate did not fly at once on the striker's peer: {crates[1].LinearVelocity}");
        Expect.True(await WaitUntil(() => crates[0].Net().Authority.Peer == 2 && crates[0].GlobalPosition.Z > 0.3f, 3),
            $"the host never saw it: authority {crates[0].Net().Authority.Peer}, at {crates[0].GlobalPosition}");
    }

    [Test]
    public async Task StrikingAPlayerPushesItOnItsOwnPeer()
    {
        var pushed = new List<(int Peer, Vector3 Impulse)>();
        var targets = new[] { Walker(Host, 1, new Vector3(3, 1, 0), Vector3.Zero), Walker(Client, 1, new Vector3(3, 1, 0), Vector3.Zero) };
        foreach (var target in targets)
            target.Net().Pushed += impulse => pushed.Add((target.Multiplayer.GetUniqueId(), impulse));
        Walker(Host, 2, new Vector3(-3, 1, 0), Vector3.Zero);
        var striker = Walker(Client, 2, new Vector3(-3, 1, 0), Vector3.Zero);
        for (var i = 0; i < 5; i++) await NextFrame();

        striker.Push(targets[1], new Vector3(4, 0, 0));
        Expect.True(await WaitUntil(() => pushed.Count > 0, 3), "never pushed");
        for (var i = 0; i < 10; i++) await NextFrame();
        Expect.SequenceEqual([(1, new Vector3(4, 0, 0))], pushed);
        Expect.Equal(1, targets[1].Net().Authority.Peer, "a player is never taken");
    }

    [Test]
    public async Task PushStrengthPushesWhatTheCharacterWalksInto()
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
        Expect.True(with > without + 0.5f, $"with PushStrength the crate moved to {with}, without to {without}");
    }

    [Test]
    public async Task ACrateRestingOnOneThisPeerStillSimulatesDoesNotGoBackAlone()
    {
        // The top crate settles while the bottom one is held. Handed back on its own, it would be the host's, frozen
        // here, and hang in the air for a network delay the moment the bottom one is lifted away
        NetworkObject[] Stack(NetfoxStack stack) =>
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
        NetworkObject[] Stack(NetfoxStack stack) =>
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
        Expect.True(onClient[0].Release(), "could not release the bottom crate");

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
        NetworkObject[] Pair(NetfoxStack stack) =>
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
        onClient.AuthorityChanged += () => dropped.Add($"authority {onClient.Authority.Peer} holder {onClient.Holder}");

        await WaitUntil(() => onClient.PendingRequest == 0, 3);
        for (var i = 0; i < 20; i++) await NextFrame();
        Expect.True(onClient.Holder == 2 && dropped.Count == 0, $"the claim was undone on the claimer: {string.Join("; ", dropped)}");
    }
}
