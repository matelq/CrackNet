using System.ComponentModel;
using Godot;

namespace Netfox.Internal;

/// <summary>Implemented by generated code: hands the spawn arguments that arrived as a Variant to the typed OnSpawned.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface ISpawnArgsReceiver
{
    void ReceiveSpawnArgs(Variant args);
}
