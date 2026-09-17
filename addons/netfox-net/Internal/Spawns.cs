using Godot;
using Netfox.Core.Logging;
using Netfox.Core.Serialization;

namespace Netfox.Internal;

/// <summary>
/// Spawning replicated scenes without a <see cref="MultiplayerSpawner"/> per spawning peer. The spawning peer instances
/// the scene and tells everyone its scene path, parent, name, authority and transform; a joiner gets every live one.
/// There is no registry of scenes: clients are trusted. Nothing else travels with a spawn - a new object stays hidden
/// until its first sample brings its state.
/// <para>
/// Freeing is sent by the authority, or by the host, when the root is queued for deletion; an object's authority frees
/// it only after <see cref="NetworkObject.Despawn"/>'s grace period, so observers have played it to the end by then.
/// Objects a peer spawned and still simulates, and that nobody else may take, leave with that peer.
/// </para>
/// </summary>
internal sealed class Spawns
{
    private static readonly NetfoxLogger Logger = NetfoxLogger.ForNetfox("Spawns");

    private readonly NetworkObjectServer _server;
    private readonly NetworkCommandServer.Command _cmdSpawn;
    private readonly NetworkCommandServer.Command _cmdFree;
    private readonly Dictionary<Node, string> _scenes = new(ReferenceEqualityComparer.Instance);
    private int _counter;

    public Spawns(NetworkObjectServer server)
    {
        _server = server;
        var commands = server.Context.NetworkCommandServer;
        _cmdSpawn = commands.RegisterCommandAt(CommandIds.ObjectSpawn, HandleSpawn, MultiplayerPeer.TransferModeEnum.Reliable);
        _cmdFree = commands.RegisterCommandAt(CommandIds.ObjectFree, HandleFree, MultiplayerPeer.TransferModeEnum.Reliable);
    }

    private MultiplayerApi Multiplayer => _server.Multiplayer;

    private Node MultiplayerRoot
        => Multiplayer is SceneMultiplayer { RootPath: var path } && !path.IsEmpty && _server.GetNodeOrNull(path) is { } root
            ? root
            : _server.GetTree().Root;

    public T Spawn<T>(Node parent, PackedScene scene, Action<T>? setup, int authority) where T : Node
    {
        var local = Multiplayer.GetUniqueId();
        if (authority == 0) authority = local;

        var root = scene.Instantiate<T>();
        root.Name = $"{System.IO.Path.GetFileNameWithoutExtension(scene.ResourcePath)}_{local}_{++_counter}";
        root.SetMultiplayerAuthority(authority);
        setup?.Invoke(root);
        parent.AddChild(root);

        _scenes[root] = scene.ResourcePath;
        if (Multiplayer.MultiplayerPeer is not (null or OfflineMultiplayerPeer)) SendSpawn(root, 0);
        return root;
    }

    private void SendSpawn(Node root, int peer)
    {
        var writer = new ByteWriter();
        writer.PutUtf8String(MultiplayerRoot.GetPathTo(root.GetParent()));
        writer.PutUtf8String(_scenes[root]);
        writer.PutUtf8String(root.Name);
        VarUint.Encode(root.GetMultiplayerAuthority(), writer);
        CompactValues.Encode(root is Node3D or Node2D ? root.Get("transform") : default, writer);
        _cmdSpawn.Send(writer.ToArray(), peer);
    }

    /// <summary>On the host: every live spawned object, to a peer that just joined.</summary>
    public void SendAllTo(int peer)
    {
        if (!Multiplayer.IsServer()) return;
        foreach (var root in _scenes.Keys)
            if (GodotObject.IsInstanceValid(root) && root.IsInsideTree()) SendSpawn(root, peer);
    }

    private void HandleSpawn(int sender, byte[] data)
    {
        var reader = new ByteReader(data);
        var parentPath = reader.GetUtf8String();
        var scenePath = reader.GetUtf8String();
        var name = reader.GetUtf8String();
        var authority = VarUint.DecodeInt(reader);
        var transform = CompactValues.Decode(reader);

        if (MultiplayerRoot.GetNodeOrNull(parentPath) is not { } parent)
        {
            Logger.Warning("Spawn of {0} from #{1}: no parent at {2}", name, sender, parentPath);
            return;
        }
        if (parent.HasNode(name)) return;
        if (ResourceLoader.Load<PackedScene>(scenePath) is not { } scene)
        {
            Logger.Error("Spawn of {0} from #{1}: cannot load {2}", name, sender, scenePath);
            return;
        }

        var root = scene.Instantiate();
        root.Name = name;
        root.SetMultiplayerAuthority(authority);
        if (transform.VariantType != Variant.Type.Nil) root.Set("transform", transform);
        parent.AddChild(root);
        _scenes[root] = scenePath;
    }

    /// <summary>A registered object's root left the tree: forget it, and tell everyone if it was freed on purpose.</summary>
    public void Deregistered(NetworkObject obj)
    {
        var root = obj.Root!;
        if (!_scenes.Remove(root)) return;
        if (!root.IsQueuedForDeletion() || !(obj.Authority.IsLocal || Multiplayer.IsServer())) return;
        if (Multiplayer.MultiplayerPeer is null or OfflineMultiplayerPeer) return;

        var writer = new ByteWriter();
        writer.PutUtf8String(MultiplayerRoot.GetPathTo(root));
        _cmdFree.Send(writer.ToArray(), 0);
    }

    private void HandleFree(int sender, byte[] data)
    {
        var path = new ByteReader(data).GetUtf8String();
        if (MultiplayerRoot.GetNodeOrNull(path) is { } root && !root.IsQueuedForDeletion()) root.QueueFree();
    }

    /// <summary>What a leaving peer spawned and still simulates, that nobody else may take, goes with it.</summary>
    public void ErasePeer(int peer)
    {
        foreach (var root in _scenes.Keys.ToArray())
        {
            if (!GodotObject.IsInstanceValid(root) || root.GetMultiplayerAuthority() != peer) continue;
            if (NetworkObject.Of(root) is { Transferable: true }) continue;
            root.QueueFree();
        }
    }
}
