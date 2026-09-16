using Godot;

namespace Netfox.Tests;

public partial class SyncedNode : Node3D
{
    [Synced] public Vector3 Velocity { get; set; }
    [Synced(Interpolate = false)] public float Charge { get; set; }
    [Synced] public int Health { get; set; }
    public int NotSynced { get; set; }
}

public partial class SyncedPropertyTests : TestSuite
{
    [Test]
    public void GeneratorListsSyncedPropertiesWithTheirInterpolation()
    {
        using var node = new SyncedNode();
        var properties = ((ISyncedProperties)node).GetSyncedProperties().ToList();

        Expect.Equal(3, properties.Count);
        Expect.True(properties.Contains(new SyncedProperty("Velocity", true)));
        Expect.True(properties.Contains(new SyncedProperty("Charge", false)));
        Expect.True(properties.Contains(new SyncedProperty("Health", true)));
    }
}
