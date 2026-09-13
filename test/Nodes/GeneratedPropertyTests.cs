using Godot;

namespace Netfox.Tests;

/// <summary>
/// A node that declares its netfox properties with attributes instead of strings. The generator implements the
/// declaring interfaces on it; nothing here spells a property name out by hand.
/// </summary>
public partial class AttributedNode : Node3D, IRollbackTick
{
    [RollbackState] public int Health { get; set; }

    [RollbackState] public Vector3 Velocity { get; set; }

    [RollbackInput] public Vector3 Movement { get; set; }

    [SynchronizedState] public int Score { get; set; }

    [Interpolated] public float Alpha { get; set; }

    public void RollbackTick(double delta, int tick, bool isFresh) => Health -= 1;
}

/// <summary>
/// #22: the property paths a synchronizer works from used to be strings, checked at runtime if at all. Renaming a
/// property left the string behind and the node silently stopped replicating. These cases pin down that the generator
/// derives them from the members instead.
/// </summary>
public partial class GeneratedPropertyTests : TestSuite
{
    public override Task AfterCase()
    {
        FreeChildren();
        return Task.CompletedTask;
    }

    [Test]
    public async Task AttributesDeclareTheProperties()
    {
        var node = await Mount(new AttributedNode { Name = "Attributed" });

        // Bare names, the way the interfaces are meant to report them: the synchronizer prefixes the node path
        Expect.SequenceEqual(["Health", "Velocity"], ((IRollbackStateProperties)node).GetRollbackStateProperties());
        Expect.SequenceEqual(["Movement"], ((IRollbackInputProperties)node).GetRollbackInputProperties());
        Expect.SequenceEqual(["Score"], ((ISynchronizedStateProperties)node).GetSynchronizedStateProperties());
        Expect.SequenceEqual(["Alpha"], ((IInterpolatedProperties)node).GetInterpolatedProperties());
    }

    /// <summary>
    /// An attribute the scene does not list is not replicated - the editor gathers attributes into the lists on
    /// save, and at no other time. That used to be silent (netfox-net#58). The synchronizer now says which paths it
    /// was given attributes for and does not carry.
    /// </summary>
    [Test]
    public async Task AnAttributeTheSceneDoesNotListIsNamed()
    {
        var node = await Mount(new AttributedNode { Name = "Unlisted" });
        var synchronizer = new RollbackSynchronizer
        {
            Name = "RollbackSynchronizer",
            Root = node,
            StateProperties = [":Health"],   // Velocity declared, not listed
            InputProperties = [],            // Movement declared, not listed
        };
        node.AddChild(synchronizer);
        await NextFrame();

        Expect.SequenceEqual([":Velocity", ":Movement"], synchronizer.UnlistedAttributeProperties);

        // And nothing to say once the scene lists what the code declares
        synchronizer.StateProperties = [":Health", ":Velocity"];
        synchronizer.InputProperties = [":Movement"];
        synchronizer.ProcessSettings();
        Expect.Empty(synchronizer.UnlistedAttributeProperties);
    }

    /// <summary>The generated paths have to be the ones a synchronizer can actually resolve, not just well formed.</summary>
    [Test]
    public async Task GeneratedPathsResolveToTheRealProperties()
    {
        var node = await Mount(new AttributedNode { Name = "Resolved", Health = 7, Velocity = Vector3.Up });

        foreach (var name in ((IRollbackStateProperties)node).GetRollbackStateProperties())
        {
            var entry = PropertyEntry.Parse(node, $":{name}");
            Expect.True(entry.IsValid(), $"{name} should resolve on the node that declared it");
        }

        Expect.VariantEqual(7, PropertyEntry.Parse(node, ":Health").GetValue());
        Expect.VariantEqual(Vector3.Up, PropertyEntry.Parse(node, ":Velocity").GetValue());
    }

    /// <summary>And the synchronizer picks them up through the editor gather, which is what the interfaces are for.</summary>
    [Test]
    public async Task SynchronizerGathersTheGeneratedProperties()
    {
        var node = new AttributedNode { Name = "Gathered" };
        var synchronizer = new RollbackSynchronizer { Name = "RBS", Root = node };
        node.AddChild(synchronizer);
        await Mount(node);

        synchronizer._GetConfigurationWarnings();

        Expect.SequenceEqual([":Health", ":Velocity"], synchronizer.StateProperties);
        Expect.SequenceEqual([":Movement"], synchronizer.InputProperties);
    }
}
