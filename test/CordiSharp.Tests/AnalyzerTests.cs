using System.Collections.Immutable;
using CordiSharp.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace CordiSharp.Tests;

public class AnalyzerTests
{
    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(MetadataReference (p) => MetadataReference.CreateFromFile(p))
            .Concat([MetadataReference.CreateFromFile(typeof(Context).Assembly.Location)])
            .ToArray();

    private static ImmutableArray<Diagnostic> Analyze(string source)
    {
        var compilation = CSharpCompilation.Create(
            "AnalyzerTest",
            [CSharpSyntaxTree.ParseText(source)],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var analyzer = new CordiSharpAnalyzer();
        var withAnalyzers = compilation.WithAnalyzers([analyzer]);
        return withAnalyzers.GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public void EmptyInjectName_ReportsError()
    {
        var diagnostics = Analyze("""
            using CordiSharp;
            [Inject("")]
            public class BadPlugin : CordiSharp.Service
            {
                public BadPlugin(Context ctx) : base(ctx, "bad") { }
            }
            """);
        Assert.Contains(diagnostics, d => d.Id == "CORDIS002");
    }

    [Fact]
    public void PluginWithoutPublicCtor_ReportsError()
    {
        var diagnostics = Analyze("""
            using CordiSharp;
            [Plugin("x")]
            public class NoCtorPlugin : IPlugin<object>
            {
                private NoCtorPlugin() { }
                public void Load(Context ctx, object config) { }
            }
            """);
        Assert.Contains(diagnostics, d => d.Id == "CORDIS003");
    }

    [Fact]
    public void UnsupportedConfigProperty_ReportsWarning()
    {
        var diagnostics = Analyze("""
            using CordiSharp;
            using CordiSharp.Schema;
            [Plugin("x")]
            public class FuncConfigPlugin : IPlugin<FuncConfig>
            {
                public void Load(Context ctx, FuncConfig config) { }
            }
            [PluginConfig]
            public class FuncConfig
            {
                public Func<int>? Callback { get; set; }
            }
            """);
        Assert.Contains(diagnostics, d => d.Id == "CORDIS001");
    }

    [Fact]
    public void ValidPlugin_NoDiagnostics()
    {
        var diagnostics = Analyze("""
            using CordiSharp;
            [Plugin("x")]
            public class GoodPlugin : IPlugin<object>
            {
                public GoodPlugin() { }
                public void Load(Context ctx, object config) { }
            }
            """);
        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("CORDIS"));
    }
}