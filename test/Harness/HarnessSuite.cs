using Godot;

namespace Netfox.Tests;

/// <summary>
/// A host and a client in one SceneTree, talking over a <see cref="LoopbackNetwork"/>. Each gets its own
/// <see cref="NetfoxContext"/> and its own MultiplayerAPI, so the two stacks are as separate as two processes.
/// </summary>
public abstract partial class HarnessSuite : TestSuite
{
    protected LoopbackNetwork Network { get; private set; } = null!;
    protected NetfoxStack Host { get; private set; } = null!;
    protected NetfoxStack Client { get; private set; } = null!;

    private readonly List<NetfoxStack> _stacks = new();
    private NetfoxSettings _settingsBackup = null!;

    public override async Task BeforeCase()
    {
        // Sync faster than the defaults so a case does not have to wait seconds for the initial clock handshake.
        // The servers read settings when they are constructed, which happens below.
        _settingsBackup = NetfoxSettings.Instance;
        var settings = NetfoxSettings.Load();
        settings.SyncInterval = 0.02;
        settings.SyncSamples = 4;
        settings.SyncAdjustSteps = 2;
        NetfoxSettings.Instance = settings;

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

        NetfoxSettings.Instance = _settingsBackup;
        await NextFrame();
    }

    protected NetfoxStack CreateStack(string name, int peerId)
    {
        var stack = NetfoxStack.Create(this, name, Network, peerId);
        _stacks.Add(stack);
        return stack;
    }

    /// <summary>Replaces the network the stacks talk over, the way rejoining from a lobby does.</summary>
    protected void ReplaceNetwork(LoopbackNetwork network) => Network = network;
}
