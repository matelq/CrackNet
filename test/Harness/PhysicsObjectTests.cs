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

    private static Walker Walker(NetfoxStack stack, int peer, Vector3 position, Vector3 velocity)
    {
        var walker = new Walker { Name = $"Walker{peer}", Position = position, Walk = velocity };
        walker.SetMultiplayerAuthority(peer);
        walker.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.4f, Height = 1.8f } });
        walker.AddChild(new NetworkObject { Name = "NetworkObject" });
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
    public async Task AKnockPushesARigidBodyOnItsAuthorityAndRaisesKnockedOnACharacter()
    {
        var crates = new[] { Crate(Host, "Crate", new Vector3(0, 0.5f, 0)), Crate(Client, "Crate", new Vector3(0, 0.5f, 0)) };
        var walkers = new[] { Walker(Host, 2, new Vector3(5, 1, 0), Vector3.Zero), Walker(Client, 2, new Vector3(5, 1, 0), Vector3.Zero) };
        var knocked = new List<(int Peer, Vector3 Impulse)>();
        foreach (var walker in walkers)
            Net(walker).Knocked += impulse => knocked.Add((walker.Multiplayer.GetUniqueId(), impulse));
        await NextFrame();

        Net(crates[1]).Knock(new Vector3(0, 0, 8));
        Net(walkers[0]).Knock(new Vector3(3, 0, 0));

        Expect.True(await WaitUntil(() => crates[0].LinearVelocity.Z > 1 && knocked.Count > 0, 3),
            $"crate velocity {crates[0].LinearVelocity}, knocked {knocked.Count}");
        for (var i = 0; i < 10; i++) await NextFrame();
        Expect.SequenceEqual([(2, new Vector3(3, 0, 0))], knocked);
    }

    [Test]
    public async Task AThrowFliesOnTheThrowersSimulation()
    {
        var crates = new[] { Crate(Host, "Crate", new Vector3(0, 0.5f, 0)), Crate(Client, "Crate", new Vector3(0, 0.5f, 0)) };
        await NextFrame();

        Expect.True(Net(crates[1]).TryGrab());
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
}
