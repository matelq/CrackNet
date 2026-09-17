using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Netfox.SourceGenerators;

namespace Netfox.Core.Tests.Generators;

public class SyncedPropertiesGeneratorTests
{
    private static IReadOnlyList<Diagnostic> Run(string source)
    {
        // Stand-ins for what the addon declares, so the generated code compiles without Godot
        const string netfox = """
            namespace Netfox;
            public readonly record struct SyncedProperty(string Path, bool Interpolate);
            public interface ISyncedProperties { System.Collections.Generic.IEnumerable<SyncedProperty> GetSyncedProperties(); }
            """;
        var compilation = CSharpCompilation.Create("Test",
            [CSharpSyntaxTree.ParseText(netfox), CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        CSharpGeneratorDriver.Create(new SyncedPropertiesGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
        return diagnostics;
    }

    [Fact]
    public void ATopLevelPartialTypeGeneratesWithoutDiagnostics()
        => Assert.Empty(Run("namespace Game; public partial class Crate { [Netfox.Synced] public int Hp { get; set; } }"));

    [Theory]
    [InlineData("namespace Game; public partial class Level { public partial class Crate { [Netfox.Synced] public int Hp { get; set; } } }")]
    [InlineData("namespace Game; public partial class Pickup<T> { [Netfox.Synced] public int Hp { get; set; } }")]
    public void ANestedOrGenericTypeIsAnError(string source)
        // Generated at namespace level without type parameters, the code declares a different, empty type: the real
        // one replicates nothing and compiles fine
        => Assert.Contains(Run(source), diagnostic => diagnostic.Id == "NFX002");
}
