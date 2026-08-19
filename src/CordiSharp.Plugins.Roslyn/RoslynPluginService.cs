using CordiSharp.Importing.Generated;
using CordiSharp.Loading;
using CordiSharp.Registry;
using Microsoft.CodeAnalysis;

namespace CordiSharp.Plugins.Roslyn;

/// <summary>A CordiSharp service that compiles plugin C# source at runtime and loads the
/// resulting assembly through <see cref="AssemblyLoaderService"/> (the <c>"loader"</c>
/// plugin), i.e. into a collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/>.
/// <para>Declares an <c>[Inject("loader")]</c> dependency: load the loader plugin first
/// (<c>await root.Plugin(typeof(AssemblyLoaderService))</c>), then load this service
/// (<c>await root.Plugin(typeof(RoslynPluginService))</c>). If the loader is not yet
/// available the fiber stays <see cref="FiberState.Pending"/> and activates automatically
/// as soon as it appears. Other plugins can <c>[Inject("roslyn")]</c> this service.</para></summary>
[Inject("loader", Alias = "Loader")]
[Service("roslyn")]
public sealed class RoslynPluginService(Context ctx) : Service(ctx)
{
    /// <summary>Compiles a plugin source string. Throws <see cref="RoslynCompilationException"/>
    /// on error-level diagnostics.</summary>
    public static CompiledPluginAssembly Compile(string source, RoslynCompileOptions? options = null)
        => CSharpPluginCompiler.Compile(source, options);

    /// <summary>Compiles a plugin source string without throwing (see
    /// <see cref="CSharpPluginCompiler.TryCompile"/>).</summary>
    public static bool TryCompile(string source, out CompiledPluginAssembly? compiled,
        out IReadOnlyList<Diagnostic> errors, RoslynCompileOptions? options = null)
        => CSharpPluginCompiler.TryCompile(source, out compiled, out errors, options);

    /// <summary>Compiles a plugin source string and immediately loads the resulting
    /// assembly through the injected <see cref="AssemblyLoaderService"/>, returning the
    /// <see cref="AssemblyPluginSet"/>. Load individual plugins with
    /// <c>set.LoadPlugin(name, config)</c> and unload everything with
    /// <c>await set.UnloadAsync()</c>.</summary>
    public AssemblyPluginSet CompileAndLoad(string source, RoslynCompileOptions? options = null)
        => LoadCompiled(CSharpPluginCompiler.Compile(source, options), options);

    /// <summary>Loads an already-compiled plugin assembly through
    /// <see cref="AssemblyLoaderService"/>, returning the <see cref="AssemblyPluginSet"/>.
    /// Useful when a compiled image is cached and re-loaded later.</summary>
    public AssemblyPluginSet LoadCompiled(CompiledPluginAssembly compiled, RoslynCompileOptions? options = null)
        => Ctx.Loader.LoadAssembly(
            compiled.AssemblyBytes,
            compiled.PdbBytes,
            name: compiled.AssemblyName,
            directory: options?.DepsDirectory);
}
