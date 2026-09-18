using System.Collections.Immutable;
using CrackNet.SourceGenerators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace CrackNet.Core.Tests.Generators;

public class SpawnGeneratorTests
{
    private const string ProjectDir = "C:/game/";

    // Stand-ins for Godot and the addon, enough for the generator's symbol checks
    private const string Stubs = """
        namespace Godot
        {
            public class GodotObject { }
            public class Node : GodotObject { }
            public class Node3D : Node { }
            public class Node2D : Node { }
            public class CharacterBody3D : Node3D { }
            public class RigidBody3D : Node3D { }
            public struct Color { }
            public struct Vector3 { }
        }
        namespace CrackNet
        {
            public interface ISpawnedWith<in TArgs> { void OnSpawned(TArgs args); }
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

    private static string RootScene(string script) => $$"""
        [gd_scene load_steps=2 format=3]

        [ext_resource type="Script" path="{{script}}" id="1"]

        [node name="Root" type="Node3D"]
        script = ExtResource("1")
        """;

    private static (IReadOnlyList<Diagnostic> Diagnostics, string Generated) Run(string source, params (string Path, string Text)[] scenes)
        => Run([(source, "game/Thing.cs")], scenes);

    private static (IReadOnlyList<Diagnostic> Diagnostics, string Generated) Run((string Source, string Path)[] sources, params (string Path, string Text)[] scenes)
    {
        var compilation = CSharpCompilation.Create("Test",
            sources.Select(s => CSharpSyntaxTree.ParseText(s.Source, path: ProjectDir + s.Path)).Prepend(CSharpSyntaxTree.ParseText(Stubs)),
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var driver = CSharpGeneratorDriver.Create(
            [new SpawnGenerator().AsSourceGenerator()],
            scenes.Select(scene => (AdditionalText)new Scene(ProjectDir + scene.Path, scene.Text)).ToImmutableArray(),
            optionsProvider: new Options());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        var generated = string.Join("\n", output.SyntaxTrees.Select(tree => tree.ToString()).Where(text => text.Contains("public static")));
        return (diagnostics, generated);
    }

    [Fact]
    public void AClassWithItsSceneGetsSpawnFromANodeAndAtATransform()
    {
        var (diagnostics, generated) = Run(
            "namespace Game; [CrackNet.Scene] public partial class Thing : Godot.RigidBody3D { }",
            ("game/Thing.tscn", RootScene("res://game/Thing.cs")));

        Assert.Empty(diagnostics);
        Assert.Contains("public static Thing Spawn(global::Godot.Node3D from, global::Godot.Node? parent = null, int authority = 0)", generated);
        Assert.Contains("public static Thing Spawn(global::Godot.Transform3D at, global::Godot.Node? parent = null, int authority = 0)", generated);
    }

    [Fact]
    public void SpawnDataIsRequiredUnlessOnSpawnedDeclaresADefault()
    {
        var (_, required) = Run(
            "namespace Game; public partial class Thing : Godot.Node3D, CrackNet.ISpawnedWith<float> { public void OnSpawned(float fuse) { } }",
            ("game/Thing.tscn", RootScene("res://game/Thing.cs")));
        Assert.Contains("Spawn(global::Godot.Transform3D at, float args, global::Godot.Node? parent", required);

        var (_, optional) = Run(
            "namespace Game; public partial class Thing : Godot.Node3D, CrackNet.ISpawnedWith<Godot.Color> { public void OnSpawned(Godot.Color color = default) { } }",
            ("game/Thing.tscn", RootScene("res://game/Thing.cs")));
        Assert.Contains("Spawn(global::Godot.Transform3D at, global::Godot.Color args = default, global::Godot.Node? parent", optional);
    }

    [Fact]
    public void AClassWithoutASceneIsAnError()
        => Assert.Contains(Run(
                "namespace Game; [CrackNet.Scene] public partial class Thing : Godot.Node3D { }",
                ("game/Other.tscn", RootScene("res://game/Other.cs"))).Diagnostics,
            diagnostic => diagnostic.Id == "CRN003");

    [Fact]
    public void AClassAtTheRootOfTwoScenesIsAnError()
        => Assert.Contains(Run(
                "namespace Game; [CrackNet.Scene] public partial class Thing : Godot.Node3D { }",
                ("game/Thing.tscn", RootScene("res://game/Thing.cs")),
                ("game/Rocket.tscn", RootScene("res://game/Thing.cs"))).Diagnostics,
            diagnostic => diagnostic.Id == "CRN004");

    [Fact]
    public void SpawnDataThatDoesNotFitAVariantIsAnError()
        => Assert.Contains(Run(
                "namespace Game; public record struct Charge(float Value); public partial class Thing : Godot.Node3D, CrackNet.ISpawnedWith<Charge> { public void OnSpawned(Charge charge) { } }",
                ("game/Thing.tscn", RootScene("res://game/Thing.cs"))).Diagnostics,
            diagnostic => diagnostic.Id == "CRN005");

    [Fact]
    public void APartialClassSpanningFilesFindsTheSceneNextToTheFileNamedAfterIt()
    {
        var (diagnostics, generated) = Run(
            [
                ("namespace Game; public partial class Thing { private void Extra() { } }", "game/parts/AnotherPart.cs"),
                ("namespace Game; [CrackNet.Scene] public partial class Thing : Godot.Node3D { }", "game/Thing.cs"),
            ],
            ("game/Thing.tscn", RootScene("res://game/Thing.cs")));

        Assert.Empty(diagnostics);
        Assert.Contains("public static Thing Spawn(", generated);
    }
}
