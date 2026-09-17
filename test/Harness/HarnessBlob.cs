using Godot;

namespace Netfox.Tests;

/// <summary>A replicated node whose state is as large as <see cref="Blob"/> makes it, for packet size cases.</summary>
public partial class HarnessBlob : Node3D
{
    [Synced] public string Blob { get; set; } = "";

    public NetworkObject Object { get; private set; } = null!;

    public static HarnessBlob Spawn(Node stack, string name, int authority, string blob)
    {
        var node = new HarnessBlob { Name = name, Blob = blob };
        node.SetMultiplayerAuthority(authority);
        node.Object = new NetworkObject { Name = "NetworkObject", Kind = NetworkObject.ObjectKind.Custom };
        node.AddChild(node.Object);
        stack.AddChild(node);
        return node;
    }
}
