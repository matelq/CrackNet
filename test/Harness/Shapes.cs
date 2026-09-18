using Godot;

namespace CrackNet.Tests;

/// <summary>
/// A collision shape built in code, with the C# handle on its shape released at once, on the main thread. Left to the
/// .NET finalizer, the handle outlives the node, and the finalizer's thread frees the shape in the physics server:
/// Rapier panics on any thread but the main one, and a panicking server left worlds half built for the next case.
/// </summary>
internal static class Shapes
{
    public static CollisionShape3D Collision(Shape3D shape)
    {
        var collision = new CollisionShape3D { Shape = shape };
        shape.Dispose();
        return collision;
    }
}
