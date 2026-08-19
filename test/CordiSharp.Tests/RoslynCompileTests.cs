using CordiSharp.Importing.Generated;
using CordiSharp.Loading;
using CordiSharp.Plugins.Roslyn;
using CordiSharp.Registry;
using Xunit;

// the import generator mirrors the CordiSharp framework service "loader" (AssemblyLoaderService)
[assembly: Import("loader")]

namespace CordiSharp.Tests;

/// <summary>Tests for the runtime-compilation plugin pipeline
/// (<see cref="RoslynPluginService"/> + <see cref="CSharpPluginCompiler"/>): C# source is
/// compiled in memory with Roslyn, loaded through <see cref="AssemblyLoaderService"/> into a
/// collectible ALC, run and unloaded. The roslyn service declares <c>[Inject("loader")]</c>,
/// so the loader plugin is loaded first (see <see cref="Start"/>) or the fiber waits for it
/// (see <see cref="Inject_Loader_DependencyActivatesWhenLoaderAppears"/>).</summary>
public class RoslynCompileTests
{
    private const string CounterSource = """
        using CordiSharp;
        using CordiSharp.Registry;
        using CordiSharp.Schema;

        [Plugin("runtime-counter")]
        public sealed class RuntimeCounterPlugin : IPlugin<RuntimeCounterConfig>
        {
            public void Load(Context ctx, RuntimeCounterConfig config)
            {
                ctx.Provide("runtime-counter", config.Start);
            }
        }

        [PluginConfig]
        public sealed class RuntimeCounterConfig
        {
            [DefaultValue(0)]
            public int Start { get; set; }
        }
        """;

    /// <summary>Loads the loader plugin first (the roslyn service declares
    /// <c>[Inject("loader")]</c>), then the roslyn service.</summary>
    private static async Task<(Context Root, RoslynPluginService Roslyn)> Start()
    {
        var root = Context.Create();
        var loaderHandle = root.Plugin(typeof(AssemblyLoaderService));
        await loaderHandle;
        var handle = root.Plugin(typeof(RoslynPluginService));
        await handle;
        var roslyn = root.Get<RoslynPluginService>("roslyn")!;
        return (root, roslyn);
    }

    [Fact]
    public async Task Inject_Loader_DependencyActivatesWhenLoaderAppears()
    {
        var root = Context.Create();
        // loader not loaded yet: the [Inject("loader")] fiber stays pending and the
        // service is not provided yet (its ctor only runs once the fiber activates)
        var roslynHandle = root.Plugin(typeof(RoslynPluginService));
        Assert.Equal(FiberState.Pending, roslynHandle.State);
        Assert.Null(root.Get<RoslynPluginService>("roslyn", strict: false));

        // providing the loader flips the epoch and activates the fiber automatically
        var loaderHandle = root.Plugin(typeof(AssemblyLoaderService));
        await loaderHandle;
        await roslynHandle;

        Assert.Equal(FiberState.Active, roslynHandle.State);
        Assert.NotNull(root.Get<RoslynPluginService>("roslyn", strict: false));

        // unloading the loader cascades back: the inject is no longer satisfied, so the
        // roslyn service is unloaded and unregistered
        await root.RegistryDeleteAsync(typeof(AssemblyLoaderService));
        await roslynHandle;
        Assert.Equal(FiberState.Pending, roslynHandle.State);
        Assert.Null(root.Get<RoslynPluginService>("roslyn", strict: false));
    }

    [Fact]
    public async Task CompileAndLoad_RunsPlugin_WithDictionaryConfig()
    {
        var (root, roslyn) = await Start();
        await using var set = roslyn.CompileAndLoad(CounterSource);

        var handle = set.LoadPlugin("runtime-counter", new Dictionary<string, object?> { ["Start"] = 42 });
        await handle;

        Assert.Equal(FiberState.Active, handle.State);
        Assert.Equal(42, root.Get<int>("runtime-counter"));
    }

    [Fact]
    public async Task CompileAndLoad_ServiceSubclass_BridgeWorks()
    {
        const string source = """
            using CordiSharp;
            using CordiSharp.Registry;

            [Service("hello2")]
            public sealed class Hello2Service(Context ctx) : Service(ctx)
            {
                public string Hello2(string name) => $"Hi, {name}!";
            }
            """;

        var (_, roslyn) = await Start();
        await using var set = roslyn.CompileAndLoad(source);

        var handle = set.LoadPlugin("Hello2Service");
        await handle;

        // host-side contract; the bridge adapts to the plugin service by method name/arity
        var bridge = set.GetService<IHello2Contract>("hello2");
        Assert.Equal("Hi, cordis!", bridge.Hello2("cordis"));

        await set.UnloadAsync(verify: false);
        Assert.Throws<PluginUnloadedException>(() => bridge.Hello2("cordis"));
    }

    /// <summary>Host-side contract used by <see cref="CompileAndLoad_ServiceSubclass_BridgeWorks"/>.</summary>
    public interface IHello2Contract
    {
        string Hello2(string name);
    }

    [Fact]
    public async Task CompileAndLoad_WithExtraReference_CanUseHostTypes()
    {
        const string source = """
            using CordiSharp;
            using CordiSharp.Registry;

            [Plugin("extra-ref")]
            public sealed class ExtraRefPlugin : IPlugin<object>
            {
                public void Load(Context ctx, object config)
                    => ctx.Provide("extra-ref", CordiSharp.Tests.RoslynExtraHelper.Value);
            }
            """;

        var (root, roslyn) = await Start();
        // the test assembly is NOT in the built-in whitelist; pass it explicitly
        var options = new RoslynCompileOptions
        {
            ExtraReferencePaths = [typeof(RoslynCompileTests).Assembly.Location],
        };
        await using var set = roslyn.CompileAndLoad(source, options);

        var handle = set.LoadPlugin("extra-ref");
        await handle;

        Assert.Equal("extra-ok", root.Get<string>("extra-ref"));
    }

    [Fact]
    public async Task CompileAndLoad_ThenUnload_Detaches()
    {
        var (root, roslyn) = await Start();
        var set = roslyn.CompileAndLoad(CounterSource);
        var handle = set.LoadPlugin("runtime-counter", new Dictionary<string, object?> { ["Start"] = 1 });
        await handle;
        Assert.Equal(1, root.Get<int>("runtime-counter"));

        await set.UnloadAsync(verify: false);

        Assert.True(set.IsUnloaded);
        Assert.Null(set.AssemblyLoadContext);
        Assert.Empty(set.Plugins);
        Assert.Null(root.Get("runtime-counter"));
        Assert.True(handle.IsUnloaded);
    }

    [Fact]
    public async Task LoadCompiled_ReusesCompiledAssembly()
    {
        var (_, roslyn) = await Start();
        var compiled = RoslynPluginService.Compile(CounterSource);
        Assert.True(compiled.Success);
        Assert.NotNull(compiled.PdbBytes);

        await using var set1 = roslyn.LoadCompiled(compiled);
        await using var set2 = roslyn.LoadCompiled(compiled);

        Assert.Equal("runtime-counter", set1.Plugins[0].Name);
        Assert.Equal(set1.AssemblyPath, set2.AssemblyPath); // same assembly name
        Assert.NotSame(set1.AssemblyLoadContext, set2.AssemblyLoadContext); // independent ALCs
    }

    [Fact]
    public async Task ImportAccessor_ExposesLoader()
    {
        var root = Context.Create();
        await root.Plugin(typeof(AssemblyLoaderService));
        await root.Plugin(typeof(RoslynPluginService));
        var roslyn = root.Get<RoslynPluginService>("roslyn")!;

        // ctx.Loader is GENERATED by CordiSharpImportGenerator from [assembly: Import("loader")]:
        // the mirror interface exposes AssemblyLoaderService's public members (framework
        // types like AssemblyPluginSet stay usable because the impl lives in CordiSharp)
        var compiled = RoslynPluginService.Compile(CounterSource);
        // the mirrored interface drops default parameter values; pass them explicitly
        await using var set = root.Loader.LoadAssembly(
            compiled.AssemblyBytes, compiled.PdbBytes, compiled.AssemblyName, null);

        Assert.True(root.Loader.LoadedCount > 0);
        var handle = set.LoadPlugin("runtime-counter", new Dictionary<string, object?> { ["Start"] = 5 });
        await handle;
        Assert.Equal(5, root.Get<int>("runtime-counter"));
    }

    [Fact]
    public async Task Compile_SyntaxError_ThrowsWithDiagnostics()
    {
        var (_, roslyn) = await Start();
        var exception = Assert.Throws<RoslynCompilationException>(
            () => RoslynPluginService.Compile("class Broken { void M() => ; }"));
        Assert.NotEmpty(exception.Errors);
        Assert.All(exception.Errors, d => Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public void TryCompile_ReturnsErrors_WithoutThrowing()
    {
        Assert.False(CSharpPluginCompiler.TryCompile("class Broken {", out var compiled, out var errors));
        Assert.Null(compiled);
        Assert.NotEmpty(errors);

        Assert.True(CSharpPluginCompiler.TryCompile(CounterSource, out compiled, out errors));
        Assert.NotNull(compiled);
        Assert.Empty(errors);
    }

}

/// <summary>Helper type in the (host) test assembly, referenced by plugin source via
/// <see cref="RoslynCompileOptions.ExtraReferencePaths"/>.</summary>
public static class RoslynExtraHelper
{
    public const string Value = "extra-ok";
}
