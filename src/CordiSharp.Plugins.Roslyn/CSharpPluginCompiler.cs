using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace CordiSharp.Plugins.Roslyn;

/// <summary>Compiles plugin C# source code into an assembly in memory using Roslyn. The
/// produced PE image (and portable PDB) is fed to
/// <see cref="Loading.AssemblyLoaderService.LoadAssembly(byte[], byte[], string?, string?)"/>
/// which places it in a collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/>;
/// plugins are then discovered by reflection (no source generator involvement).
/// <para>Sources are full compilation units — classes with <c>[Plugin]</c>, <c>[Service]</c>
/// subclasses or <c>IPlugin</c> implementations — and must carry their own <c>using</c>
/// directives (runtime compilation has no implicit usings; <see cref="RoslynCompileOptions.AddDefaultUsings"/>
/// prepends a common set).</para></summary>
public static class CSharpPluginCompiler
{
    private static int _assemblyCounter;

    private const string DefaultGlobalUsings = 
        """
        global using System;
        global using System.Collections.Generic;
        global using System.Linq;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    /// <summary>Compiles a single plugin source string. Throws
    /// <see cref="RoslynCompilationException"/> on error-level diagnostics.</summary>
    public static CompiledPluginAssembly Compile(string source, RoslynCompileOptions? options = null)
        => Compile([source], options);

    /// <summary>Compiles multiple plugin source strings as one assembly. Throws
    /// <see cref="RoslynCompilationException"/> on error-level diagnostics.</summary>
    public static CompiledPluginAssembly Compile(IEnumerable<string> sources, RoslynCompileOptions? options = null)
    {
        var opts = options ?? new RoslynCompileOptions();
        var trees = new List<SyntaxTree>();
        var parseOptions = GetParseOptions(opts);
        if (opts.AddDefaultUsings)
        {
            trees.Add(CSharpSyntaxTree.ParseText(DefaultGlobalUsings, parseOptions));
        }
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            trees.Add(CSharpSyntaxTree.ParseText(source, parseOptions));
        }
        return Compile(trees, opts);
    }

    /// <summary>Compiles pre-parsed syntax trees as one assembly. Trees are used verbatim
    /// (no default usings are prepended). Throws <see cref="RoslynCompilationException"/>
    /// on error-level diagnostics.</summary>
    public static CompiledPluginAssembly Compile(IReadOnlyList<SyntaxTree> trees, RoslynCompileOptions? options = null)
    {
        var opts = options ?? new RoslynCompileOptions();
        var assemblyName = opts.AssemblyName ?? $"CordiSharp.Plugins.Runtime.{Interlocked.Increment(ref _assemblyCounter)}";
        var compilation = CSharpCompilation.Create(
            assemblyName,
            trees,
            BuildReferences(opts),
            new CSharpCompilationOptions(
                opts.OutputKind,
                optimizationLevel: opts.OptimizationLevel,
                allowUnsafe: opts.AllowUnsafe,
                nullableContextOptions: opts.NullableContextOptions,
                deterministic: opts.Deterministic));

        using var peStream = new MemoryStream();
        EmitResult emitResult;
        byte[]? pdbBytes = null;
        if (opts.EmitPdb)
        {
            using var pdbStream = new MemoryStream();
            emitResult = compilation.Emit(peStream, pdbStream,
                options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
            if (emitResult.Success) pdbBytes = pdbStream.ToArray();
        }
        else
        {
            emitResult = compilation.Emit(peStream);
        }

        return emitResult.Success
            ? new CompiledPluginAssembly(assemblyName, peStream.ToArray(), pdbBytes, emitResult.Diagnostics)
            : throw new RoslynCompilationException(emitResult.Diagnostics);
    }

    /// <summary>Compiles a plugin source string without throwing: on success
    /// <paramref name="compiled"/> is set and <paramref name="errors"/> is empty; on
    /// failure <paramref name="compiled"/> is null and <paramref name="errors"/> holds the
    /// error-level diagnostics.</summary>
    public static bool TryCompile(string source, out CompiledPluginAssembly? compiled,
        out IReadOnlyList<Diagnostic> errors, RoslynCompileOptions? options = null)
    {
        try
        {
            compiled = Compile(source, options);
            errors = [];
            return true;
        }
        catch (RoslynCompilationException exception)
        {
            compiled = null;
            errors = exception.Errors;
            return false;
        }
    }

    private static CSharpParseOptions GetParseOptions(RoslynCompileOptions options)
        => CSharpParseOptions.Default
            .WithLanguageVersion(options.LanguageVersion)
            .WithPreprocessorSymbols(options.PreprocessorSymbols ?? []);

    /// <summary>Builds the reference set: a whitelist (the runtime's trusted platform
    /// assemblies — the BCL/shared-framework surface, including the System.Runtime contract
    /// that framework metadata references — plus CordiSharp core and the entry assembly) and
    /// caller-supplied <see cref="RoslynCompileOptions.ExtraReferencePaths"/> and
    /// <see cref="RoslynCompileOptions.ExtraReferences"/>. References are deduplicated.
    /// Assemblies without a file location (e.g. single-file publish) are skipped — pass them
    /// explicitly via <see cref="RoslynCompileOptions.ExtraReferences"/> instead.</summary>
    private static List<MetadataReference> BuildReferences(RoslynCompileOptions options)
    {
        var result = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // built-in whitelist: the runtime's trusted platform assemblies (the BCL / shared
        // framework surface, including contract assemblies such as System.Runtime that
        // CordiSharp's metadata references), plus CordiSharp core and the entry assembly
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) AddFile(path);
            }
        }
        AddAssembly(typeof(Context).Assembly);
        AddAssembly(Assembly.GetEntryAssembly());

        if (options.ExtraReferencePaths is not null)
        {
            foreach (var path in options.ExtraReferencePaths) AddFile(path);
        }
        if (options.ExtraReferences is not null)
        {
            foreach (var reference in options.ExtraReferences)
            {
                if (reference.Display is not null && !seen.Add(reference.Display)) continue;
                result.Add(reference);
            }
        }
        return result;

        void AddAssembly(Assembly? assembly)
        {
            if (assembly is null || assembly.IsDynamic) return;
            AddFile(assembly.Location);
        }

        void AddFile(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            if (!seen.Add(Path.GetFileName(path))) return;
            result.Add(MetadataReference.CreateFromFile(path));
        }
    }
}
