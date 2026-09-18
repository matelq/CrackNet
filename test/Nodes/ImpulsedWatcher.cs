// Built in code, not from a scene: CRN006 has nothing to check here
#pragma warning disable CRN006

using Godot;

namespace CrackNet.Tests;

/// <summary>A character that writes down every push the library hands it through <see cref="IImpulsed"/>.</summary>
public partial class ImpulsedWatcher : CharacterBody3D, IImpulsed
{
    public List<Vector3> Heard { get; } = new();

    public void OnImpulsed(Vector3 impulse) => Heard.Add(impulse);
}
