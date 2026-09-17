using Godot;

namespace Netfox.Tests;

/// <summary>What NetworkObject reads from the type of its root: the kind, what is sent, and which roots it refuses.</summary>
public partial class ObjectKindTests : TestSuite
{
    public override Task AfterCase()
    {
        FreeChildren();
        return Task.CompletedTask;
    }

    [Test]
    public void AutoResolvesFromTheRootsType()
    {
        Expect.Equal(NetworkObject.ObjectKind.Personal, NetworkObject.KindFor(new CharacterBody3D()));
        Expect.Equal(NetworkObject.ObjectKind.Personal, NetworkObject.KindFor(new CharacterBody2D()));
        Expect.Equal(NetworkObject.ObjectKind.Personal, NetworkObject.KindFor(new Node3D()));
        Expect.Equal(NetworkObject.ObjectKind.Shared, NetworkObject.KindFor(new RigidBody3D()));
        Expect.Equal(NetworkObject.ObjectKind.Shared, NetworkObject.KindFor(new VehicleBody3D()));
        Expect.Equal(NetworkObject.ObjectKind.Shared, NetworkObject.KindFor(new RigidBody2D()));
        Expect.Equal(NetworkObject.ObjectKind.World, NetworkObject.KindFor(new AnimatableBody3D()));
        Expect.Equal(NetworkObject.ObjectKind.World, NetworkObject.KindFor(new StaticBody2D()));
        Expect.Equal(NetworkObject.ObjectKind.World, NetworkObject.KindFor(new Area3D()));
        Expect.Equal(NetworkObject.ObjectKind.World, NetworkObject.KindFor(new Node()));
    }

    [Test]
    public async Task AKindSetsWhoMayTakeTheObjectAndWhetherItPassesItOn()
    {
        async Task<NetworkObject> Mounted(Node root, NetworkObject.ObjectKind kind)
        {
            var obj = new NetworkObject { Name = "NetworkObject", Kind = kind };
            root.AddChild(obj);
            await Mount(root);
            return obj;
        }

        var personal = await Mounted(new CharacterBody3D { Name = "Player" }, NetworkObject.ObjectKind.Auto);
        Expect.False(personal.Transferable);
        Expect.True(personal.SpreadsAuthority);

        var shared = await Mounted(new RigidBody3D { Name = "Crate" }, NetworkObject.ObjectKind.Auto);
        Expect.True(shared.Transferable);
        Expect.True(shared.SpreadsAuthority);

        var world = await Mounted(new AnimatableBody3D { Name = "Lift" }, NetworkObject.ObjectKind.Auto);
        Expect.False(world.Transferable);
        Expect.False(world.SpreadsAuthority);

        var grenade = await Mounted(new RigidBody3D { Name = "Grenade" }, NetworkObject.ObjectKind.Personal);
        Expect.False(grenade.Transferable);
    }

    [Test]
    public async Task WhatIsSentIsListedInOrder()
    {
        var crate = new RigidBody3D { Name = "Crate" };
        var obj = new NetworkObject { Name = "NetworkObject" };
        crate.AddChild(obj);
        await Mount(crate);
        Expect.Equal("global_transform\nlinear_velocity\nangular_velocity", obj.SyncedSummary);

        var blob = new HarnessBlob { Name = "Blob" };
        var blobObject = new NetworkObject { Name = "NetworkObject" };
        blob.AddChild(blobObject);
        await Mount(blob);
        Expect.Equal("global_transform\nBlob", blobObject.SyncedSummary);
    }

    [Test]
    public void AnUnsupportedRootIsAnErrorOnTheNode()
    {
        var soft = new SoftBody3D();
        var obj = new NetworkObject();
        soft.AddChild(obj);
        Expect.NotNull(NetworkObject.UnsupportedReason(soft));
        Expect.Equal(1, obj._GetConfigurationWarnings().Length);

        obj.Kind = NetworkObject.ObjectKind.Custom;
        Expect.Equal(0, obj._GetConfigurationWarnings().Length);
        Expect.Null(NetworkObject.UnsupportedReason(new RigidBody3D()));
        soft.Free();
    }
}
