using Godot;
using Netfox.Extras;

namespace Netfox.Examples;

/// <summary>
/// #12: the Rapier drivers are ported from upstream's <c>.off</c> files and drive the extension through ClassDB, which
/// cannot be checked without the extension actually installed. This runs the driver against a real one.
/// <para>
/// Manual, not part of the test suite: it needs appsinacup/godot-rapier-physics dropped into <c>addons/</c> and
/// <c>physics/3d/physics_engine="Rapier3D"</c> in project.godot. With neither, it says so and exits 0, so a run
/// without the extension is a skip rather than a failure.
/// </para>
/// <para>
/// Run: <c>godot --headless --path . res://examples/physics/RapierCheck.tscn</c>
/// </para>
/// </summary>
public partial class RapierCheck : Node3D
{
    private const int Ticks = 40;
    private const int RollbackTo = 10;

    private RapierPhysicsDriver3D _driver = null!;
    private NetworkRigidBody3D _body = null!;
    private readonly Dictionary<int, float> _heights = new();
    private bool _done;

    public override async void _Ready()
    {
        if (!RapierPhysicsDriver3D.IsAvailable)
        {
            GD.Print("RAPIER CHECK skipped=True reason=extension-not-installed " +
                     $"server={ClassDB.ClassExists("RapierPhysicsServer3D")} state_manager={ClassDB.ClassExists("StateManager3D")}");
            GetTree().Quit(0);
            return;
        }

        var engine = (string)ProjectSettings.GetSetting("physics/3d/physics_engine", "DEFAULT");
        GD.Print($"RAPIER CHECK engine={engine}");

        var ground = new StaticBody3D { Name = "Ground", Position = new Vector3(0, -4, 0) };
        ground.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(20, 1, 20) } });
        AddChild(ground);

        _body = new NetworkRigidBody3D { Name = "Falling Body", Position = new Vector3(0, 4, 0) };
        _body.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.5f } });
        AddChild(_body);

        _driver = new RapierPhysicsDriver3D { Name = "Rapier Driver" };
        AddChild(_driver);

        Multiplayer.MultiplayerPeer = new OfflineMultiplayerPeer();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        NetworkTime.Instance.AfterTickLoop += CheckDone;
        NetworkRollback.Instance.AfterProcessTick += RecordHeight;
        NetworkTime.Instance.Start();
    }

    private void RecordHeight(int tick) => _heights[tick] = _body.Position.Y;

    private void CheckDone()
    {
        if (_done || NetworkTime.Instance.Tick < Ticks) return;
        _done = true;

        var fell = _heights.Count > 0 && _heights[_heights.Keys.Max()] < 4.0f - 0.1f;

        // A rollback has to put the body back where it was: the driver reloads the whole space from Rapier's cache
        var before = _heights.GetValueOrDefault(RollbackTo, float.NaN);
        NetworkRollback.Instance.NotifyResimulationStart(RollbackTo);
        NetworkTime.Instance.RunAfterTickLoop();
        var after = _heights.GetValueOrDefault(RollbackTo, float.NaN);

        var reproduced = Mathf.Abs(before - after) < 1e-3f;

        var summary = FormattableString.Invariant(
            $"RAPIER CHECK skipped=False ticks={_heights.Count} fell={fell} height={_heights[_heights.Keys.Max()]:F3}");
        var rollbackSummary = FormattableString.Invariant(
            $"resim@{RollbackTo} before={before:F4} after={after:F4} reproduced={reproduced}");
        GD.Print($"{summary} {rollbackSummary}");

        GetTree().Quit(fell && reproduced ? 0 : 1);
    }
}
