using System.ComponentModel;
using System.Reflection;
using Godot;

namespace CrackNet;

/// <summary>
/// An object that needs data when it is created: <see cref="OnSpawned"/> runs with the same arguments on every peer,
/// late joiners included, before the root enters the tree. The generated <c>Spawn</c> of the class takes the argument,
/// optional only when <see cref="OnSpawned"/> declares a default value itself.
/// </summary>
/// <typeparam name="TArgs">What the object needs; it travels in a Godot Variant, so a Godot type, a primitive, a string,
/// an enum or a Godot collection.</typeparam>
public interface ISpawnedWith<in TArgs>
{
    void OnSpawned(TArgs args);
}

/// <summary>What generated <c>Spawn</c> methods call; game code uses those instead.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class Spawning
{
    /// <summary>
    /// Instances the scene of <typeparamref name="T"/> on every peer. <paramref name="scenePath"/> null means the scene
    /// named after the class next to its script.
    /// </summary>
    public static T Spawn<T>(string? scenePath, Variant transform, Node? from, Variant args, Node? parent, int authority)
        where T : Node
    {
        var tree = from?.GetTree() ?? parent?.GetTree() ?? Engine.GetMainLoop() as SceneTree
            ?? throw new InvalidOperationException("Spawning needs a scene tree");
        parent ??= tree.CurrentScene ?? throw new InvalidOperationException("Spawning needs a parent: there is no current scene");
        var server = CrackNetContext.For(parent).NetworkObjectServer;
        return (T)server.Spawns.Spawn(parent, scenePath ?? SceneOf(typeof(T)), transform, args, authority);
    }

    /// <summary>The scene named after a class: its script's path with <c>.tscn</c> for <c>.cs</c>.</summary>
    internal static string SceneOf(Type type)
    {
        var script = type.GetCustomAttribute<ScriptPathAttribute>()?.Path
                     ?? throw new InvalidOperationException($"{type.Name} is not a Godot script class, so it has no scene");
        return script.Substring(0, script.Length - ".cs".Length) + ".tscn";
    }
}
