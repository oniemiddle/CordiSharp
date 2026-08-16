using System.Runtime.Loader;
using CordiSharp.Importing.Generated;
using CordiSharp.Loading;
using CordiSharp.Registry;
using CordiSharp.Samples.PluginLibrary;
using Xunit;

namespace CordiSharp.Tests;

/// <summary>Tests for the assembly loader plugin (<see cref="AssemblyLoaderService"/>):
/// loading external assemblies into collectible load contexts and unloading them.</summary>
public class AssemblyLoadingTests
{
    private static string PluginLibraryPath => typeof(CounterPlugin).Assembly.Location;

    private static async Task<(Context Root, AssemblyLoaderService Loader)> StartLoader()
    {
        var root = Context.Create();
        var handle = root.Plugin(typeof(AssemblyLoaderService));
        await handle;
        var loader = root.Get<AssemblyLoaderService>("loader")!;
        return (root, loader);
    }

    private static bool WaitForUnload(WeakReference<AssemblyLoadContext> weak)
    {
        for (var i = 0; i < 30; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!weak.TryGetTarget(out _)) return true;
            Thread.Sleep(20);
        }
        return !weak.TryGetTarget(out _);
    }

    /// <summary>Some sandboxed / CI hosts cannot complete collectible-ALC collection at
    /// all (even an empty ALC stays alive after forced GC). The collection assertion is
    /// skipped there; structural unload assertions always run.</summary>
    private static bool EnvironmentSupportsAlcUnload()
    {
        var alc = new AssemblyLoadContext("probe", isCollectible: true);
        var weak = new WeakReference<AssemblyLoadContext>(alc);
        alc.Unload();
        for (var i = 0; i < 10; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!weak.TryGetTarget(out _)) return true;
        }
        return false;
    }

    [Fact]
    public async Task LoadAssembly_DiscoversPlugins()
    {
        var (_, loader) = await StartLoader();
        await using var set = loader.LoadAssembly(PluginLibraryPath);

        var names = set.Plugins.Select(p => p.Name).OrderBy(n => n).ToList();
        Assert.Contains("counter", names);
        Assert.Contains("GreeterService", names);
        Assert.Contains("DependentPlugin", names);
        Assert.Equal("counter", set.GetPlugin("counter").Name);
        Assert.NotNull(set.GetPlugin("counter").ConfigType);
    }

    [Fact]
    public async Task LoadPlugin_Runs_WithDictionaryConfig()
    {
        var (root, loader) = await StartLoader();
        await using var set = loader.LoadAssembly(PluginLibraryPath);

        var handle = set.LoadPlugin("counter", new Dictionary<string, object?> { ["Start"] = 10 });
        await handle;

        Assert.Equal(FiberState.Active, handle.State);
        Assert.Equal(10, root.Get<int>("counter"));
    }

    [Fact]
    public async Task LoadPlugin_Runs_WithHostPocoConfig()
    {
        var (root, loader) = await StartLoader();
        await using var set = loader.LoadAssembly(PluginLibraryPath);

        // the host-side CounterConfig is a different type identity than the one inside the
        // collectible assembly; the loader materializes an instance of the plugin's own type
        var handle = set.LoadPlugin("counter", new CounterConfig { Start = 7 });
        await handle;

        Assert.Equal(7, root.Get<int>("counter"));
    }

    [Fact]
    public async Task Inject_AcrossAssemblyBoundary()
    {
        var (_, loader) = await StartLoader();
        await using var set = loader.LoadAssembly(PluginLibraryPath);

        var greeter = set.LoadPlugin("GreeterService");
        await greeter;
        var dependent = set.LoadPlugin("DependentPlugin",
            new Dictionary<string, object?> { ["Message"] = "cross-assembly" });
        await dependent;

        Assert.Equal(FiberState.Active, dependent.State);
    }

    [Fact]
    public async Task Unload_DisposesAndDetaches()
    {
        var (root, loader) = await StartLoader();
        var set = loader.LoadAssembly(PluginLibraryPath);
        var handle = set.LoadPlugin("counter", new Dictionary<string, object?> { ["Start"] = 1 });
        await handle;
        Assert.NotNull(set.AssemblyLoadContext);
        Assert.Equal(1, root.Get<int>("counter"));

        await set.UnloadAsync(verify: false);

        // structural unload: fiber disposed, service removed, set inert, handle detached
        Assert.True(set.IsUnloaded);
        Assert.Null(set.AssemblyLoadContext);
        Assert.Empty(set.Plugins);
        Assert.Null(root.Get("counter"));
        Assert.True(handle.IsUnloaded);
        Assert.Throws<ObjectDisposedException>(() => handle.Update(null));
    }

    [Fact]
    public async Task Unload_CollectsAssembly()
    {
        var (_, loader) = await StartLoader();
        var set = loader.LoadAssembly(PluginLibraryPath);
        var weak = new WeakReference<AssemblyLoadContext>(set.AssemblyLoadContext!);
        var handle = set.LoadPlugin("counter", new Dictionary<string, object?> { ["Start"] = 1 });
        await handle;

        await set.UnloadAsync(verify: false);

        Assert.True(handle.IsUnloaded);
        // collection is verified only where the host supports it (see
        // EnvironmentSupportsAlcUnload); structural unload is asserted above
        if (EnvironmentSupportsAlcUnload())
        {
            Assert.True(WaitForUnload(weak),
                "the collectible ALC should be collected after UnloadAsync (no strong references remain)");
        }
    }

    [Fact]
    public async Task StaticRegistry_NotPolluted_ByCollectibleTypes()
    {
        var (_, loader) = await StartLoader();
        await using var set = loader.LoadAssembly(PluginLibraryPath);

        // the ALC plugin type is a different Type object than the host-side copy and must
        // never be registered in the static registry (that would root the assembly forever)
        var alcType = set.GetPlugin("counter").Type!;
        Assert.Null(PluginMetadataRegistry.Get(alcType));
    }

    [Fact]
    public async Task LoaderUnload_CascadesToLoadedAssemblies()
    {
        var root = Context.Create();
        var loaderHandle = root.Plugin(typeof(AssemblyLoaderService));
        await loaderHandle;
        var loader = root.Get<AssemblyLoaderService>("loader")!;
        var set = loader.LoadAssembly(PluginLibraryPath);
        var weak = new WeakReference<AssemblyLoadContext>(set.AssemblyLoadContext!);
        await set.LoadPlugin("counter", new Dictionary<string, object?> { ["Start"] = 3 });

        await root.RegistryDeleteAsync(typeof(AssemblyLoaderService));

        Assert.True(set.IsUnloaded);
        if (EnvironmentSupportsAlcUnload())
        {
            Assert.True(WaitForUnload(weak), "unloading the loader plugin should cascade to its assemblies");
        }
    }

    [Fact]
    public async Task GetService_ReturnsBridge_ThatWorksThenThrowsAfterUnload()
    {
        var (_, loader) = await StartLoader();
        var set = loader.LoadAssembly(PluginLibraryPath);
        await set.LoadPlugin("GreeterService");

        // host-defined contract: the bridge adapts to the plugin's internal service by
        // method name/arity (GreeterService.Greet) without a compile-time reference
        IGreeterContract bridge = set.GetService<IGreeterContract>("greeter");
        Assert.Equal("Hello, cordis!", bridge.Greet("cordis"));

        await set.UnloadAsync(verify: false);

        Assert.Throws<PluginUnloadedException>(() => bridge.Greet("cordis"));
    }

    [Fact]
    public async Task GetService_ValidatesContractAndPresence()
    {
        var (_, loader) = await StartLoader();
        var set = loader.LoadAssembly(PluginLibraryPath);
        await set.LoadPlugin("GreeterService");

        Assert.Throws<CordisException>(() => set.GetService<object>("greeter"));
        Assert.Throws<ServiceResolutionException>(() => set.GetService<IGreeterContract>("missing"));
    }

    /// <summary>Host-side contract used by <see cref="GetService_ReturnsBridge_ThatWorksThenThrowsAfterUnload"/>.
    /// The plugin type (GreeterService) does not implement it — the bridge adapts by name.</summary>
    public interface IGreeterContract
    {
        string Greet(string name);
    }

    [Fact]
    public async Task ImportAccessor_ResolvesGeneratedContract()
    {
        var (root, loader) = await StartLoader();
        var set = loader.LoadAssembly(PluginLibraryPath);
        await set.LoadPlugin("GreeterService");

        // IGreeterService + ctx.greeter are GENERATED by CordiSharpImportGenerator from
        // [Import("greeter")] (see ImportMarker below) — no hand-written contract; the
        // generated bridge forwards to the plugin service loaded in the collectible ALC
        var greeter = root.greeter;
        Assert.Equal("Hello, cordis!", greeter.Greet("cordis"));

        await set.UnloadAsync(verify: false);
        Assert.Throws<PluginUnloadedException>(() => greeter.Greet("cordis"));
    }

    /// <summary>Annotation consumed by the import source generator.</summary>
    [Import("greeter")]
    public static class ImportMarker { }
}
