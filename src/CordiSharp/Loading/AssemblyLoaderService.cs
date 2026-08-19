using System.Reflection;
using System.Runtime.Loader;
using CordiSharp.Registry;

namespace CordiSharp.Loading;

/// <summary>A plugin service that loads external plugin assemblies into collectible
/// <see cref="AssemblyLoadContext"/>s and unloads them. It is a normal CordiSharp plugin:
/// load it with <c>await root.Plugin(typeof(AssemblyLoaderService))</c>, then other plugins
/// can <c>[Inject("loader")]</c> it (the service registers under the name <c>"loader"</c>).
/// Unloading the loader service itself cascades to every assembly it loaded.</summary>
[Service("loader")]
public sealed class AssemblyLoaderService(Context ctx) : Service(ctx)
{
    private readonly List<AssemblyPluginSet> _sets = [];

    /// <summary>Number of assemblies currently loaded by this service.</summary>
    public int LoadedCount => _sets.Count;

    /// <summary>Loads an external plugin assembly into a new collectible load context and
    /// discovers its plugins (<c>[Plugin]</c> classes, <see cref="Service"/> subclasses and
    /// <see cref="IPlugin"/> implementations).</summary>
    public AssemblyPluginSet LoadAssembly(string assemblyPath)
    {
        var fullPath = Path.GetFullPath(assemblyPath);
        var name = "plugin:" + Path.GetFileNameWithoutExtension(fullPath);
        var directory = Path.GetDirectoryName(fullPath) ?? ".";
        var alc = new PluginAssemblyLoadContext(name, directory);
        Assembly assembly;
        try
        {
            assembly = alc.LoadFromAssemblyPath(fullPath);
        }
        catch (Exception error)
        {
            alc.Unload();
            throw new CordisException($"""cannot load assembly "{fullPath}": {error.Message}""", error);
        }

        var plugins = Discover(assembly);
        var catalog = LoadCatalog(assembly);
        var set = new AssemblyPluginSet(this, name, fullPath, alc, assembly, plugins, catalog);
        _sets.Add(set);
        return set;
    }

    private static int _scriptCounter;

    /// <summary>Loads a plugin assembly compiled in memory (e.g. by the Roslyn plugin
    /// compiler) into a new collectible load context and discovers its plugins. Works like
    /// <see cref="LoadAssembly(string)"/> but reads the PE image (and optional portable PDB)
    /// from byte arrays, so no file is written to disk. <paramref name="name"/> becomes the
    /// load-context display name (default: a generated script name);
    /// <paramref name="directory"/> is the directory probed for plugin dependencies
    /// (default: the application base directory). The assembly bytes and PDB are retained in
    /// memory until the set is unloaded.</summary>
    public AssemblyPluginSet LoadAssembly(byte[] assemblyBytes, byte[]? pdbBytes = null,
        string? name = null, string? directory = null)
    {
        if (assemblyBytes is null || assemblyBytes.Length == 0)
        {
            throw new CordisException("cannot load assembly from an empty byte array");
        }
        var alcName = name ?? $"plugin:script-{Interlocked.Increment(ref _scriptCounter)}";
        var dir = directory is null ? AppContext.BaseDirectory : Path.GetFullPath(directory);
        var alc = new PluginAssemblyLoadContext(alcName, dir);
        var asmStream = new MemoryStream(assemblyBytes);
        var pdbStream = pdbBytes is null ? null : new MemoryStream(pdbBytes);
        Assembly assembly;
        try
        {
            assembly = alc.LoadFromStream(asmStream, pdbStream);
        }
        catch (Exception error)
        {
            alc.Unload();
            throw new CordisException($"""cannot load in-memory assembly "{alcName}": {error.Message}""", error);
        }

        var plugins = Discover(assembly);
        var catalog = LoadCatalog(assembly);
        var set = new AssemblyPluginSet(this, alcName, alcName, alc, assembly, plugins, catalog);
        // keep the streams alive for the lifetime of the assembly: the runtime may read
        // the PDB stream lazily, so dropping it could break symbol resolution
        set.RetainedStreams = pdbStream is null ? [asmStream] : [asmStream, pdbStream];
        _sets.Add(set);
        return set;
    }

    /// <summary>Unloads an assembly: disposes every plugin fiber created from it, drops all
    /// strong references into the assembly and unloads its load context. When
    /// <paramref name="verify"/> is true (default), a bounded forced-GC loop checks that the
    /// context is actually collected and throws <see cref="AssemblyUnloadException"/> if
    /// strong references to its types are still held.</summary>
    public async ValueTask UnloadAsync(AssemblyPluginSet set, bool verify = true)
    {
        if (set.IsUnloaded) return;
        var alc = set.ALC ?? throw new CordisException("assembly set is already unloaded");
        var weak = new WeakReference<AssemblyLoadContext>(alc);

        foreach (var runtime in set.Runtimes.ToList())
        {
            foreach (var fiber in runtime.Fibers.Snapshot())
            {
                await fiber.DisposePluginAsync();
                // buffered log messages hold the fiber; drop them so it can be collected
                Ctx.LoggerService.DropFiberLogs(fiber);
            }
        }

        set.MarkUnloaded();
        _sets.Remove(set);

        alc.Unload();
        // drop the strong local reference before the verification loop: the GC loop
        // cannot collect the ALC while this frame/state-machine still roots it
        alc = null!;
        if (verify && !WaitForUnload(weak))
        {
            throw new AssemblyUnloadException(
                $"""assembly "{set.AssemblyPath}" could not be unloaded: strong references to its types are still held. """ +
                "Release every AssemblyPluginHandle / PluginHandle, config object, AssemblyPluginInfo and Type " +
                "obtained from this assembly before unloading, and make sure no plugin code is still running.");
        }
    }

    internal AssemblyPluginHandle LoadPlugin(AssemblyPluginSet set, AssemblyPluginInfo info, object? config)
    {
        var type = info.Type ?? throw new CordisException($"""plugin "{info.Name}" belongs to an unloaded assembly""");
        var runtime = AssemblyPluginRuntimeFactory.Create(type);
        config = AssemblyPluginRuntimeFactory.MaterializeConfig(info.ConfigType, info.ConfigSchema, config);
        set.Runtimes.Add(runtime);
        Ctx.Registry.RegisterRuntime(type, runtime);
        var fiber = new Fiber(Ctx, config, runtime.Inject, runtime);
        return new AssemblyPluginHandle(fiber, set);
    }

    protected override object Init() => Disposer.From(async Task () =>
    {
        // best-effort cascade: no strict GC verification during shutdown
        foreach (var set in _sets.ToList())
        {
            await UnloadAsync(set, verify: false);
        }
    });

    /// <summary>Reads the generated service catalog (<c>CordiSharp.Generated.PluginServiceCatalog</c>)
    /// from the plugin assembly, if present. Entries are scoped to the returned list — never
    /// registered in a static registry — so they are dropped together with the set on unload.</summary>
    private static List<ServiceCatalogEntry> LoadCatalog(Assembly assembly)
    {
        var result = new List<ServiceCatalogEntry>();
        var catalogType = assembly.GetType("CordiSharp.Generated.PluginServiceCatalog");
        if (catalogType is null) return result;
        var method = catalogType.GetMethod("GetEntries", BindingFlags.Public | BindingFlags.Static);
        if (method is null || method.Invoke(null, null) is not IEnumerable<ServiceCatalogEntry> entries) return result;
        result.AddRange(entries);
        return result;
    }

    private static List<AssemblyPluginInfo> Discover(Assembly assembly)
    {
        var result = new List<AssemblyPluginInfo>();
        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract) continue;
            var isPlugin = type.GetCustomAttribute<PluginAttribute>(inherit: false) is not null
                || typeof(Service).IsAssignableFrom(type)
                || typeof(IPlugin).IsAssignableFrom(type);
            if (!isPlugin) continue;

            var attr = type.GetCustomAttributes(typeof(PluginAttribute), inherit: false).FirstOrDefault() as PluginAttribute;
            var name = attr?.Name ?? type.Name;
            var configType = attr?.ConfigType ?? FindConfigType(type);
            var schema = configType is not null ? Schema.Schema.FromType(configType) : null;
            var injects = type.GetCustomAttributes(typeof(InjectAttribute), inherit: true)
                .Cast<InjectAttribute>()
                .Select(i => i.Name)
                .ToList();
            result.Add(new AssemblyPluginInfo(name, type, configType, schema, injects));
        }
        return result;
    }

    private static Type? FindConfigType(Type type)
    {
        foreach (var iface in type.GetInterfaces())
        {
            if (!iface.IsGenericType) continue;
            var def = iface.GetGenericTypeDefinition();
            if (def == typeof(IPlugin<>) || def == typeof(IAsyncPlugin<>))
            {
                return iface.GetGenericArguments()[0];
            }
        }
        return null;
    }

    private static bool WaitForUnload(WeakReference<AssemblyLoadContext> weak)
    {
        for (var i = 0; i < 10; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!weak.TryGetTarget(out _)) return true;
            Thread.Sleep(20);
        }
        return !weak.TryGetTarget(out _);
    }
}
