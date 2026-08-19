using Microsoft.CodeAnalysis;

namespace CordiSharp.Plugins.Roslyn;

/// <summary>Thrown by <see cref="CSharpPluginCompiler"/> when the plugin source fails to
/// compile. Carries the full diagnostic list so callers can surface precise errors.</summary>
public sealed class RoslynCompilationException(IReadOnlyList<Diagnostic> diagnostics)
    : CordisException(Format(diagnostics))
{
    /// <summary>All diagnostics produced by the failed compilation (errors and warnings).</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;

    /// <summary>Error-level diagnostics only (convenience).</summary>
    public IReadOnlyList<Diagnostic> Errors => Diagnostics
        .Where(d => d.Severity == DiagnosticSeverity.Error)
        .ToList();

    private static string Format(IReadOnlyList<Diagnostic> diagnostics)
    {
        var lines = diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"  - {d}");
        return "plugin source failed to compile:\n" + string.Join("\n", lines);
    }
}
