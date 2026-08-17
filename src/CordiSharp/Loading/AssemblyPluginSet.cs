using System.Reflection;
using System.Runtime.Loader;
using CordiSharp.Registry;

namespace CordiSharp.Loading;

/// <summary>An external plugin assembly loaded into a collectible
/// <see cref="AssemblyLoadContext"/> by <see cref="AssemblyLoaderService"/>. Unloading the
/// set disposes every plugin fiber created from it, drops all strong references into the
/// assembly and calls <c>Unload()</c> on its load context.</summary>
public sealed class AssemblyPluginSet : IAsyncDisposable
{
    private readonly AssemblyLoaderService _loader;
    internal PluginAssemblyLoadContext? ALC;
    internal Assembly? Assembly;
    internal readonly List<PluginRuntime> Runtimes = [];
    internal readonly List<AssemblyPluginHandle> Handles = [];
    internal readonly List<WeakReference<ServiceBridgeBase>> Bridges = [];

    internal AssemblyPluginSet(AssemblyLoaderService loader, string name, string path,
        PluginAssemblyLoadContext alc, Assembly assembly, List<AssemblyPluginInfo> plugins,
        IReadOnlyList<ServiceCatalogEntry> catalog)
    {
        _loader = loader;
        Name = name;
        AssemblyPath = path;
        ALC = alc;
        Assembly = assembly;
        Plugins = plugins;
        ServiceCatalog = catalog;
    }

    /// <summary>Display name of the load context.</summary>
    public string Name { get; }

    /// <summary>Full path of the loaded assembly file.</summary>
    public string AssemblyPath { get; }

    /// <summary>True once this set has been unloaded (all members are inert).</summary>
    public bool IsUnloaded { get; private set; }

    /// <summary>The collectible load context; null after unload. Do not retain it across
    /// <see cref="UnloadAsync"/> — it would prevent the assembly from being collected.</summary>
    public AssemblyLoadContext? AssemblyLoadContext => ALC;

    /// <summary>Plugins discovered in the assembly (empty after unload).</summary>
    public IReadOnlyList<AssemblyPluginInfo> Plugins { get; private set; }

    /// <summary>Generated service catalog: contract interface (declared outside the plugin
    /// assembly) → internal service type + service name. Empty if the plugin assembly was
    /// not built with the CordiSharp service-catalog generator. Cleared after unload;
    /// entries hold plugin-ALC types, so do not retain them.</summary>
    public IReadOnlyList<ServiceCatalogEntry> ServiceCatalog { get; private set; } = [];

    /// <summary>Finds a discovered plugin by name.</summary>
    public AssemblyPluginInfo GetPlugin(string name)
    {
        foreach (var info in Plugins)
        {
            if (info.Name == name) return info;
        }
        throw new CordisException($"""plugin "{name}" not found in assembly {Name}""");
    }

    /// <summary>Loads a discovered plugin into a new fiber under the loader service.</summary>
    public AssemblyPluginHandle LoadPlugin(string name, object? config = null)
        => _loader.LoadPlugin(this, GetPlugin(name), config);

    /// <summary>Loads a discovered plugin into a new fiber under the loader service.</summary>
    public AssemblyPluginHandle LoadPlugin(AssemblyPluginInfo info, object? config = null)
        => _loader.LoadPlugin(this, info, config);

    /// <summary>Resolves a plugin-provided service through a weak-reference bridge exposing
    /// the host-defined contract <typeparamref name="T"/>. Calls are forwarded to the
    /// plugin's internal service by method name and arity, so the plugin type does not have
    /// to implement <typeparamref name="T"/> (and must not — that would pin the assembly).
    /// The bridge holds only a weak reference to the service: retaining it never prevents
    /// unload, and once this set is unloaded every invocation throws
    /// <see cref="PluginUnloadedException"/>.</summary>
    public T GetService<T>(string name) where T : class
    {
        if (!typeof(T).IsInterface)
        {
            throw new CordisException($"{nameof(GetService)}<T> requires T to be an interface contract");
        }
        if (IsUnloaded)
        {
            throw new PluginUnloadedException($"assembly {Name} is already unloaded");
        }
        var raw = _loader.Ctx.Root.Get(name, strict: false);
        if (raw is null)
        {
            throw new ServiceResolutionException($"""service "{name}" is not provided by assembly {Name}""");
        }
        var bridge = ServiceBridge<T>.Create(raw, name);
        Bridges.Add(new WeakReference<ServiceBridgeBase>((ServiceBridgeBase)(object)bridge));
        return bridge;
    }

    /// <summary>Resolves a plugin service through the generated service catalog: finds the
    /// service that provides contract <typeparamref name="T"/> (declared outside the plugin
    /// assembly and referenced by it) and returns its bridge. Requires the plugin assembly
    /// to have been built with the CordiSharp service-catalog source generator.</summary>
    public T GetService<T>() where T : class
    {
        if (!typeof(T).IsInterface)
        {
            throw new CordisException($"{nameof(GetService)}<T> requires T to be an interface contract");
        }
        foreach (var entry in ServiceCatalog)
        {
            if (ReferenceEquals(entry.Contract, typeof(T)))
            {
                return GetService<T>(entry.ServiceName);
            }
        }
        throw new CordisException($"no plugin service in assembly {Name} provides contract {typeof(T).Name}");
    }

    /// <summary>Unloads this assembly: disposes plugin fibers, drops references, unloads the ALC.
    /// When <paramref name="verify"/> is true (default) a bounded forced-GC loop checks that
    /// the load context was actually collected.</summary>
    public ValueTask UnloadAsync(bool verify = true) => _loader.UnloadAsync(this, verify);

    /// <summary>Best-effort cleanup: disposes fibers and unloads the ALC without the
    /// strict GC verification (never throws <see cref="AssemblyUnloadException"/>).</summary>
    public ValueTask DisposeAsync() => UnloadAsync(verify: false);

    internal void MarkUnloaded()
    {
        IsUnloaded = true;
        foreach (var info in Plugins) info.Detach();
        Plugins = [];
        foreach (var handle in Handles) handle.Detach();
        Handles.Clear();
        foreach (var weak in Bridges)
        {
            if (weak.TryGetTarget(out var bridge)) bridge.Revoke();
        }
        Bridges.Clear();
        ServiceCatalog = [];
        Runtimes.Clear();
        ALC = null;
        Assembly = null;
    }

    public override string ToString() => $"AssemblyPluginSet <{Name}>";
}
