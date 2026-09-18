using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CrackNet.SourceGenerators;

/// <summary>
/// The everyday calls (<c>this.Authority</c>, <c>crate.TryClaim()</c>, <c>this.TakeImpulses(delta)</c>) need a
/// <see cref="NetworkObject"/> on the node they reach. This analyzer says so at build time instead of letting the
/// call throw at run time: the class they are used on must have a scene, and that scene's root must carry a
/// NetworkObject as a direct child.
/// <para>
/// Only classes declared in this compilation are judged; engine types say nothing about what a node will be at run
/// time (a shape query returns <c>PhysicsBody3D</c>), so calls on them are left alone. Abstract classes are skipped:
/// their scene belongs to whoever is put in the tree. A class built in code rather than instantiated from a scene —
/// tests, generated levels — suppresses the rule with <c>#pragma warning disable CRN006</c>.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NetworkedNodeAnalyzer : DiagnosticAnalyzer
{
    private const string Extensions = "CrackNet.NetworkNodeExtensions";

    private static readonly DiagnosticDescriptor NoNetworkObject = new(
        "CRN006", "A node used over the network needs a NetworkObject in its scene",
        "'{0}' {1}, so {2} has nowhere to go: add a NetworkObject as a direct child of the scene's root",
        "CrackNet", DiagnosticSeverity.Error, true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(NoNetworkObject);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var scenes = new Scenes(start.Options);
            start.RegisterOperationAction(ctx => Check(ctx, scenes), OperationKind.Invocation, OperationKind.PropertyReference);
        });
    }

    private static void Check(OperationAnalysisContext context, Scenes scenes)
    {
        var (member, receiver) = context.Operation switch
        {
            IInvocationOperation call => ((ISymbol)call.TargetMethod, Receiver(call.Instance, call.Arguments)),
            IPropertyReferenceOperation property => (property.Property, property.Instance),
            _ => (null, null),
        };
        if (member is null || receiver is null) return;
        if (!IsOurs(member)) return;

        // The receiver reaches the extension already converted to Node; the class actually written is in the syntax
        var written = context.Operation.SemanticModel?.GetTypeInfo(receiver.Syntax).Type ?? receiver.Type;
        if (written is not INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } type) return;
        if (!type.Locations.Any(location => location.IsInSource)) return;   // an engine type says nothing

        if (scenes.Problem(type) is not { } problem) return;
        context.ReportDiagnostic(Diagnostic.Create(NoNetworkObject, context.Operation.Syntax.GetLocation(),
            type.Name, problem, member.Name is "Net" ? "Net()" : $"'{member.Name}'"));
    }

    /// <summary>An extension member's receiver: the instance for a classic extension call, or its first argument.</summary>
    private static IOperation? Receiver(IOperation? instance, ImmutableArray<IArgumentOperation> arguments)
        => instance ?? (arguments.Length > 0 ? arguments[0].Value : null);

    /// <summary>
    /// Declared by the addon's extensions. A C# 14 extension member sits in a nested "extension(Node)" type of its
    /// static class, so the containing types are walked rather than compared once.
    /// </summary>
    private static bool IsOurs(ISymbol member)
    {
        for (var type = member.ContainingType; type is not null; type = type.ContainingType)
            if (type.ToDisplayString() == Extensions) return true;
        return false;
    }

    /// <summary>The scenes of the project, read from the .tscn files that come in as additional files.</summary>
    private sealed class Scenes
    {
        private static readonly Regex ScriptResource = new(@"\[ext_resource[^\]]*type=""Script""[^\]]*path=""(?<path>[^""]+)""[^\]]*id=""(?<id>[^""]+)""", RegexOptions.Compiled);
        private static readonly Regex Node = new(@"\[node name=""(?<name>[^""]*)""(?<header>[^\]]*)\](?<body>(?:[^\[]*\n)*)", RegexOptions.Compiled);

        /// <summary>Script res path -> whether one of its scenes carries a NetworkObject directly under the root.</summary>
        private readonly Dictionary<string, bool> _rootsOf = new(StringComparer.Ordinal);
        private readonly string? _projectDir;

        public Scenes(AnalyzerOptions options)
        {
            options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.GodotProjectDir", out _projectDir);
            if (string.IsNullOrEmpty(_projectDir)) return;

            foreach (var file in options.AdditionalFiles)
            {
                if (!file.Path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase)) continue;
                var content = file.GetText()?.ToString();
                if (content is null) continue;

                var ids = ScriptResource.Matches(content).Cast<Match>().ToDictionary(m => m.Groups["id"].Value, m => m.Groups["path"].Value);
                string? rootScript = null;
                var hasNetworkObject = false;
                foreach (Match node in Node.Matches(content))
                {
                    var header = node.Groups["header"].Value;
                    var script = ScriptOf(node.Groups["body"].Value, ids);
                    var isRoot = !header.Contains("parent=");
                    if (isRoot) rootScript = script;
                    // A direct child of the root, carrying the addon's NetworkObject script
                    else if (header.Contains("parent=\".\"") && script is not null && script.EndsWith("/NetworkObject.cs", StringComparison.Ordinal))
                        hasNetworkObject = true;
                }
                if (rootScript is null) continue;
                _rootsOf[rootScript] = _rootsOf.TryGetValue(rootScript, out var had) ? had || hasNetworkObject : hasNetworkObject;
            }
        }

        private static string? ScriptOf(string body, Dictionary<string, string> ids)
        {
            var match = Regex.Match(body, @"script = ExtResource\(""(?<id>[^""]+)""\)");
            return match.Success && ids.TryGetValue(match.Groups["id"].Value, out var path) ? path : null;
        }

        /// <summary>What is wrong for this class, or null when its scene is in order.</summary>
        public string? Problem(INamedTypeSymbol type)
        {
            if (_projectDir is null) return null;   // no Godot project in sight: nothing to judge against
            var script = ScriptPathOf(type);
            if (script is null) return null;        // the class is not in the project directory
            if (!_rootsOf.TryGetValue(script, out var hasNetworkObject)) return "has no scene";
            return hasNetworkObject ? null : "has a scene without a NetworkObject";
        }

        private string? ScriptPathOf(INamedTypeSymbol type)
        {
            // A partial class can span files; Godot binds the script to the one named after the class
            var file = type.Locations.Select(location => location.SourceTree?.FilePath)
                           .FirstOrDefault(path => path is not null && System.IO.Path.GetFileNameWithoutExtension(path) == type.Name)
                       ?? type.Locations.Select(location => location.SourceTree?.FilePath).FirstOrDefault(path => path is not null);
            if (string.IsNullOrEmpty(file)) return null;   // a type from a tree with no file of its own
            var full = System.IO.Path.GetFullPath(file);
            var root = System.IO.Path.GetFullPath(_projectDir!);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            return "res://" + full.Substring(root.Length).Replace('\\', '/').TrimStart('/');
        }
    }
}
