using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CordiSharp.Plugins.Roslyn;

/// <summary>Options controlling runtime compilation of plugin source code by
/// <see cref="CSharpPluginCompiler"/>. All properties have sensible defaults; pass only
/// what you want to override.</summary>
public sealed record RoslynCompileOptions
{
    /// <summary>Assembly name of the compiled plugin assembly. When null a unique name
    /// (<c>CordiSharp.Plugins.Runtime.&lt;n&gt;</c>) is generated per compile.</summary>
    public string? AssemblyName { get; init; }

    /// <summary>C# language version accepted for the plugin source. Defaults to the
    /// latest version supported by the bundled Roslyn compiler.</summary>
    public LanguageVersion LanguageVersion { get; init; } = LanguageVersion.Latest;

    /// <summary>Optimization level of the emitted assembly. Defaults to Release.</summary>
    public OptimizationLevel OptimizationLevel { get; init; } = OptimizationLevel.Release;

    /// <summary>Whether the plugin source may use <c>unsafe</c> code. Defaults to false.</summary>
    public bool AllowUnsafe { get; init; }

    /// <summary>Nullable context for the plugin source. Defaults to enabled.</summary>
    public NullableContextOptions NullableContextOptions { get; init; } = NullableContextOptions.Enable;

    /// <summary>Preprocessor symbols defined for the plugin source (e.g. <c>TRACE</c>).</summary>
    public IReadOnlyList<string>? PreprocessorSymbols { get; init; }

    /// <summary>Kind of the emitted assembly. Defaults to a class library (DLL).</summary>
    public OutputKind OutputKind { get; init; } = OutputKind.DynamicallyLinkedLibrary;

    /// <summary>Whether a portable PDB is emitted alongside the assembly (enables
    /// debugger stepping into the plugin). Defaults to true.</summary>
    public bool EmitPdb { get; init; } = true;

    /// <summary>Whether the compilation is deterministic. Defaults to true.</summary>
    public bool Deterministic { get; init; } = true;

    /// <summary>Whether <c>global using</c> directives for common namespaces (System,
    /// System.Collections.Generic, System.Linq, System.Threading, System.Threading.Tasks)
    /// are prepended to string sources. Defaults to true — runtime compilation has no
    /// implicit usings, so this mirrors the project-level ImplicitUsings behaviour.
    /// Sources passed as <see cref="Microsoft.CodeAnalysis.SyntaxTree"/>s are never
    /// modified.</summary>
    public bool AddDefaultUsings { get; init; } = true;

    /// <summary>Extra assembly file paths referenced by the plugin source, on top of the
    /// built-in whitelist (BCL + CordiSharp + entry assembly). Paths are deduplicated.</summary>
    public IReadOnlyList<string>? ExtraReferencePaths { get; init; }

    /// <summary>Extra metadata references used by the plugin source, on top of the
    /// built-in whitelist. Deduplicated by display name.</summary>
    public IReadOnlyList<MetadataReference>? ExtraReferences { get; init; }

    /// <summary>Directory probed for plugin dependencies at load time (passed to
    /// <see cref="Loading.AssemblyLoaderService.LoadAssembly(byte[], byte[], string?, string?)"/>).
    /// Defaults to the application base directory.</summary>
    public string? DepsDirectory { get; init; }
}
