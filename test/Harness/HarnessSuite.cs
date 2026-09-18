using Godot;

namespace CrackNet.Tests;

/// <summary>
/// A host and a client in one SceneTree, talking over a <see cref="LoopbackNetwork"/>. Each gets its own
/// <see cref="CrackNetContext"/> and its own MultiplayerAPI, so the stacks are as separate as separate processes.
/// <para>
/// Two of them are always there, because most cases only need a host and a client. A case that needs more calls
/// <see cref="AddPeer"/>: three is the first count at which a peer has to deal with a player that is neither its own
/// nor the authority's, and two peers can never show that.
/// </para>
/// </summary>
public abstract partial class HarnessSuite : TestSuite
{
    protected LoopbackNetwork Network { get; private set; } = null!;
    protected CrackNetStack Host { get; private set; } = null!;
    protected CrackNetStack Client { get; private set; } = null!;

    private readonly List<CrackNetStack> _stacks = new();
    private CrackNetSettings _settingsBackup = null!;

    public override async Task BeforeCase()
    {
        // Sync faster than the defaults so a case does not have to wait seconds for the initial clock handshake.
        // The servers read settings when they are constructed, which happens below.
        _settingsBackup = CrackNetSettings.Instance;
        var settings = CrackNetSettings.Load();
        settings.SyncInterval = 0.02;
        settings.SyncSamples = 4;
        settings.SyncAdjustSteps = 2;
        CrackNetSettings.Instance = settings;

        Network = new LoopbackNetwork();
        Host = CreateStack("Host", 1);
        Client = CreateStack("Client", 2);
        Network.Connect();

        await NextFrame();
        await NextFrame();
    }

    public override async Task AfterCase()
    {
        for (var i = _stacks.Count - 1; i >= 0; i--)
            _stacks[i].Teardown();
        _stacks.Clear();

        CrackNetSettings.Instance = _settingsBackup;
        await NextFrame();
    }

    protected CrackNetStack CreateStack(string name, int peerId)
    {
        var stack = CrackNetStack.Create(this, name, Network, peerId);
        _stacks.Add(stack);
        return stack;
    }

    /// <summary>
    /// Adds one more peer to the session, connected to everyone already in it. Its stack is named after its id, and
    /// it joins after the first two, exactly the way a third player does.
    /// </summary>
    protected CrackNetStack AddPeer(int peerId)
    {
        var stack = CreateStack($"Peer_{peerId}", peerId);
        Network.ConnectLate(stack.Peer);
        return stack;
    }

    /// <summary>Replaces the network the stacks talk over, the way rejoining from a lobby does.</summary>
    protected void ReplaceNetwork(LoopbackNetwork network) => Network = network;
}
