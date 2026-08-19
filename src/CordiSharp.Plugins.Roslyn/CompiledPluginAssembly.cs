using Microsoft.CodeAnalysis;

namespace CordiSharp.Plugins.Roslyn;

/// <summary>The result of compiling plugin C# source in memory: the PE image (and optional
/// portable PDB) plus the full diagnostic list. Hand the bytes to
/// <see cref="Loading.AssemblyLoaderService.LoadAssembly(byte[], byte[], string?, string?)"/>
/// (or to <see cref="RoslynPluginService"/>) to load the plugin into a collectible ALC.</summary>
public sealed record CompiledPluginAssembly(
    string AssemblyName,
    byte[] AssemblyBytes,
    byte[]? PdbBytes,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>True when the compilation produced no error-level diagnostics.</summary>
    public bool Success => Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error);

    public override string ToString() => $"CompiledPluginAssembly <{AssemblyName}> ({AssemblyBytes.Length} bytes)";
}
