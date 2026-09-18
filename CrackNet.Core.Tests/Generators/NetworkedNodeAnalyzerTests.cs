using System.Collections.Immutable;
using CrackNet.SourceGenerators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace CrackNet.Core.Tests.Generators;

/// <summary>CRN006: the everyday calls need a NetworkObject in the scene of the class they are used on.</summary>
public class NetworkedNodeAnalyzerTests
{
    private const string ProjectDir = "C:/game/";

    // Stand-ins for Godot and the addon: the extension members the analyzer looks for, on Node
    private const string Stubs = """
        namespace Godot
        {
            public class GodotObject { }
            public class Node : GodotObject { }
            public class Node3D : Node { }
            public class RigidBody3D : Node3D { }
            public class PhysicsBody3D : Node3D { }
            public struct Vector3 { }
        }
        namespace CrackNet
        {
            public sealed class NetworkObject { public ObjectAuthority Authority { get; } = new(); }
            public sealed class ObjectAuthority { public bool IsLocal => true; public int Peer => 1; public bool Take() => true; }
            public static class NetworkNodeExtensions
            {
                extension(Godot.Node node)
                {
                    public ObjectAuthority Authority => new();
                    public bool TryClaim() => true;
                    public void Impulse(Godot.Vector3 impulse) { }
                }
                public static NetworkObject Net(this Godot.Node node) => new();
            }
        }
        """;

    private sealed class Scene(string path, string text) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    private sealed class Options : AnalyzerConfigOptionsProvider
    {
        private sealed class Global : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value)
            {
                value = ProjectDir;
                return key == "build_property.GodotProjectDir";
            }
        }

        private sealed class Empty : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value)
            {
                value = "";
                return false;
            }
        }

        public override AnalyzerConfigOptions GlobalOptions { get; } = new Global();
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new Empty();
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new Empty();
    }

    private const string NetworkObjectScript = "res://addons/cracknet/NetworkObject.cs";

    private static string SceneWith(string script, bool networkObject)
    {
        var scene = $"""
            [gd_scene load_steps=3 format=3]

            [ext_resource type="Script" path="{script}" id="1"]
            [ext_resource type="Script" path="{NetworkObjectScript}" id="2"]

            [node name="Root" type="RigidBody3D"]
            script = ExtResource("1")

            [node name="CollisionShape3D" type="CollisionShape3D" parent="."]

            """;
        return networkObject
            ? scene + """
                [node name="NetworkObject" type="Node" parent="."]
                script = ExtResource("2")

                """
            : scene;
    }

    /// <summary>A scene whose NetworkObject sits under another node rather than directly under the root.</summary>
    private static string SceneWithDeepNetworkObject(string script) => $$"""
        [gd_scene load_steps=3 format=3]

        [ext_resource type="Script" path="{{script}}" id="1"]
        [ext_resource type="Script" path="{{NetworkObjectScript}}" id="2"]

        [node name="Root" type="RigidBody3D"]
        script = ExtResource("1")

        [node name="Pivot" type="Node3D" parent="."]

        [node name="NetworkObject" type="Node" parent="Pivot"]
        script = ExtResource("2")
        """;

    /// <summary>Extension members are C# 14; the default parse options are a version behind.</summary>
    private static readonly CSharpParseOptions Parse = new(LanguageVersion.Latest);

    private static IReadOnlyList<Diagnostic> Run((string Source, string Path)[] sources, params (string Path, string Text)[] scenes)
    {
        var compilation = CSharpCompilation.Create("Test",
            sources.Select(s => CSharpSyntaxTree.ParseText(s.Source, Parse, path: ProjectDir + s.Path)).Prepend(CSharpSyntaxTree.ParseText(Stubs, Parse)),
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var withAnalyzer = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new NetworkedNodeAnalyzer()),
            new AnalyzerOptions(scenes.Select(scene => (AdditionalText)new Scene(ProjectDir + scene.Path, scene.Text)).ToImmutableArray(), new Options()));
        return withAnalyzer.GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
    }

    private static IReadOnlyList<Diagnostic> Run(string source, params (string Path, string Text)[] scenes)
        => Run([(source, "game/Thing.cs")], scenes);

    private const string UsesItself = "using CrackNet; namespace Game; public partial class Thing : Godot.RigidBody3D { public bool Mine() => this.Authority.IsLocal; }";

    [Fact]
    public void ASceneWithANetworkObjectIsFine()
        => Assert.Empty(Run(UsesItself, ("game/Thing.tscn", SceneWith("res://game/Thing.cs", networkObject: true))));

    [Fact]
    public void ASceneWithoutANetworkObjectIsAnError()
    {
        var diagnostics = Run(UsesItself, ("game/Thing.tscn", SceneWith("res://game/Thing.cs", networkObject: false)));
        Assert.Equal("CRN006", Assert.Single(diagnostics).Id);
        Assert.Contains("has a scene without a NetworkObject", diagnostics[0].GetMessage());
    }

    [Fact]
    public void NoSceneAtAllIsAnError()
    {
        var diagnostics = Run(UsesItself);
        Assert.Equal("CRN006", Assert.Single(diagnostics).Id);
        Assert.Contains("has no scene", diagnostics[0].GetMessage());
    }

    [Fact]
    public void ANetworkObjectBelowTheRootDoesNotCount()
        => Assert.Equal("CRN006", Assert.Single(Run(UsesItself, ("game/Thing.tscn", SceneWithDeepNetworkObject("res://game/Thing.cs")))).Id);

    [Fact]
    public void ACallOnAnotherClassIsJudgedByThatClassesScene()
    {
        var caller = "using CrackNet; namespace Game; public partial class Player : Godot.Node3D { public void Grab(Thing thing) => thing.TryClaim(); }";
        var thing = "using CrackNet; namespace Game; public partial class Thing : Godot.RigidBody3D { }";
        var diagnostics = Run([(caller, "game/Player.cs"), (thing, "game/Thing.cs")],
            ("game/Player.tscn", SceneWith("res://game/Player.cs", networkObject: true)));
        Assert.Contains(diagnostics, d => d.Id == "CRN006" && d.GetMessage().Contains("'Thing'"));
    }

    [Fact]
    public void CallsOnEngineTypesAreLeftAlone()
    {
        var source = """
            using CrackNet;
            namespace Game;
            public partial class Thing : Godot.RigidBody3D
            {
                public void Blast(Godot.PhysicsBody3D hit) => hit.Impulse(default);
            }
            """;
        Assert.Empty(Run(source, ("game/Thing.tscn", SceneWith("res://game/Thing.cs", networkObject: true))));
    }

    [Fact]
    public void AnAbstractClassIsSkippedAndItsSubclassIsNot()
    {
        var pickup = "using CrackNet; namespace Game; public abstract partial class Pickup : Godot.RigidBody3D { protected bool Grab() => this.TryClaim(); }";
        var thing = "using CrackNet; namespace Game; public partial class Thing : Pickup { public bool Take() => this.TryClaim(); }";

        // The abstract base has no scene of its own, and calls on it are not reported
        var withScene = Run([(pickup, "game/Pickup.cs"), (thing, "game/Thing.cs")],
            ("game/Thing.tscn", SceneWith("res://game/Thing.cs", networkObject: true)));
        Assert.Empty(withScene);

        var withoutScene = Run([(pickup, "game/Pickup.cs"), (thing, "game/Thing.cs")]);
        Assert.Equal("CRN006", Assert.Single(withoutScene).Id);
    }

    [Fact]
    public void APartialClassIsJudgedByTheFileNamedAfterIt()
    {
        var main = "using CrackNet; namespace Game; public partial class Thing : Godot.RigidBody3D { public bool Mine() => this.Authority.IsLocal; }";
        var other = "using CrackNet; namespace Game; public partial class Thing { private int _count; }";
        Assert.Empty(Run([(other, "game/Bits.cs"), (main, "game/Thing.cs")],
            ("game/Thing.tscn", SceneWith("res://game/Thing.cs", networkObject: true))));
    }

    [Fact]
    public void NetIsReportedToo()
        => Assert.Equal("CRN006", Assert.Single(Run("using CrackNet; namespace Game; public partial class Thing : Godot.RigidBody3D { public object Obj() => this.Net(); }")).Id);
}
