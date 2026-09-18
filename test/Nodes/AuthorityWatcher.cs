// Built in code, not from a scene: CRN006 has nothing to check here
#pragma warning disable CRN006

using Godot;

namespace CrackNet.Tests;

/// <summary>A node that sets itself up in _Ready and records every authority notification it gets.</summary>
public partial class AuthorityWatcher : Node3D, IAuthorityChanged
{
    public bool IsSetUp { get; private set; }
    public int Notified { get; private set; }
    public int NotifiedBeforeReady { get; private set; }

    public override void _Ready() => IsSetUp = true;

    public void OnAuthorityChanged()
    {
        Notified++;
        if (!IsSetUp) NotifiedBeforeReady++;
    }
}
