using System.Reflection;

namespace CordiSharp.Registry;

/// <summary>Manages plugin runtimes and creates plugin fibers.
/// Ports cordis <c>RegistryService</c>.</summary>
public sealed class RegistryService
{
    private readonly Context _ctx;
    private int _counter;
    private readonly Dictionary<object, PluginRuntime> _internal = new();

    internal RegistryService(Context ctx) => _ctx = ctx;

    internal int NextCounter() => ++_counter;

    /// <summary>Number of registered plugin runtimes.</summary>
    public int Size => _internal.Count;

    /// <summary>Resolves a plugin object into a runtime (registering it on first use).</summary>
    public PluginHandle Plugin(Context ctx, object plugin, object? config = null)
    {
        if (!TryResolve(plugin, out var key, out var built))
        {
            throw new InvalidPluginException("invalid plugin, expect function or object with an 'apply' method, received " + (plugin?.GetType().Name ?? "null"));
        }
        ctx.Fiber.AssertActive();

        if (!_internal.TryGetValue(key, out var runtime))
        {
            runtime = built;
            _internal[key] = runtime;
        }
        var fiber = new Fiber(ctx, config, runtime.Inject, runtime);
        return new PluginHandle(fiber);
    }

    /// <summary>Creates a plugin from a callback with declared dependencies (inject).</summary>
    public PluginHandle Inject(Context ctx, IEnumerable<string> deps, Func<Context, object?, object?> callback)
    {
        return Plugin(ctx, new InjectablePlugin(deps, callback));
    }

    /// <summary>Creates a typed plugin from a class implementing <see cref="IPlugin{TConfig}"/>.</summary>
    public PluginHandle Plugin<TPlugin, TConfig>(Context ctx, TConfig? config = default) where TPlugin : class, IPlugin<TConfig>, new()
    {
        return Plugin(ctx, typeof(TPlugin), config);
    }

    internal PluginRuntime? GetRuntime(object plugin)
    {
        return TryResolve(plugin, out var key, out _) ? _internal.GetValueOrDefault(key) : null;
    }

    internal bool HasRuntime(PluginRuntime? runtime)
    {
        return runtime is not null && _internal.ContainsValue(runtime);
    }

    internal void RemoveRuntime(PluginRuntime runtime)
    {
        var keys = _internal.Where(kv => ReferenceEquals(kv.Value, runtime)).Select(kv => kv.Key).ToList();
        foreach (var key in keys) _internal.Remove(key);
    }

    /// <summary>Unregisters a plugin runtime and disposes all of its fibers.</summary>
    public bool Delete(object plugin)
    {
        if (TryResolve(plugin, out var key, out _))
        {
            if (_internal.Remove(key, out var runtime))
            {
                foreach (var fiber in runtime.Fibers.Snapshot())
                {
                    _ = fiber.DisposePluginAsync();
                }
                return true;
            }
        }
        return false;
    }

    /// <summary>Unregisters a plugin runtime and awaits the disposal of all of its fibers.</summary>
    public async Task<bool> DeleteAsync(object plugin)
    {
        if (TryResolve(plugin, out var key, out _))
        {
            if (_internal.Remove(key, out var runtime))
            {
                foreach (var fiber in runtime.Fibers.Snapshot())
                {
                    await fiber.DisposePluginAsync();
                }
                return true;
            }
        }
        return false;
    }

    internal IEnumerable<PluginRuntime> Values() => _internal.Values;

    private static bool TryResolve(object plugin, out object key, out PluginRuntime runtime)
    {
        key = null!;
        runtime = null!;
        switch (plugin)
        {
            case Type type:
                key = type;
                runtime = BuildRuntimeFromType(type);
                return true;
            case InjectablePlugin injectable:
                key = injectable;
                runtime = BuildRuntimeFromInjectable(injectable);
                return true;
            case IPluginObject obj:
                key = obj;
                runtime = BuildRuntimeFromObject(obj);
                return true;
            case Delegate d:
                key = d;
                runtime = BuildRuntimeFromDelegate(d);
                return true;
            default:
                return false;
        }
    }

    private static PluginRuntime BuildRuntimeFromType(Type type)
    {
        // prefer compile-time metadata produced by the source generator
        PluginMetadataRegistry.EnsureGeneratedRegistrations();
        var metadata = PluginMetadataRegistry.Get(type);

        var attr = type.GetCustomAttributes(typeof(PluginAttribute), inherit: false).FirstOrDefault() as PluginAttribute;
        var name = metadata?.Name ?? attr?.Name ?? type.Name;
        var configType = metadata?.ConfigType ?? attr?.ConfigType ?? FindConfigType(type);
        var runtime = new PluginRuntime
        {
            Name = name,
            PluginType = type,
            ConfigSchema = metadata?.ConfigSchema ?? (configType is not null ? Schema.Schema.FromType(configType) : null),
            Callback = (ctx, config) => PluginLoader.Load(ctx, type, config),
        };
        if (metadata is not null)
        {
            foreach (var (injectName, injectConfig) in metadata.Inject)
            {
                runtime.Inject[injectName] = injectConfig;
            }
        }
        foreach (var inject in type.GetCustomAttributes(typeof(InjectAttribute), inherit: true).Cast<InjectAttribute>())
        {
            runtime.Inject[inject.Name] = inject.Config;
        }
        return runtime;
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

    private static PluginRuntime BuildRuntimeFromDelegate(Delegate d)
    {
        var method = d.Method;
        var parameters = method.GetParameters();
        var runtime = new PluginRuntime
        {
            // compiler-generated names (lambdas) walk up to the nearest named plugin/root
            Name = method.Name.StartsWith('<') ? null : method.Name,
            Callback = (ctx, config) =>
            {
                try
                {
                    if (parameters.Length == 0) return d.DynamicInvoke(null);
                    if (parameters.Length == 1) return d.DynamicInvoke(ctx);
                    return d.DynamicInvoke(ctx, config);
                }
                catch (TargetInvocationException e)
                {
                    throw e.InnerException ?? e;
                }
            },
        };
        // config type from delegate generic args: Action<Context, TConfig> / Func<Context, TConfig, R>
        if (parameters.Length >= 2 && parameters[1].ParameterType != typeof(object))
        {
            runtime.ConfigSchema = parameters[1].ParameterType;
        }
        return runtime;
    }

    private static PluginRuntime BuildRuntimeFromObject(IPluginObject obj)
    {
        return new PluginRuntime
        {
            Name = obj.Name,
            Callback = obj.Apply,
        };
    }

    private static PluginRuntime BuildRuntimeFromInjectable(InjectablePlugin injectable)
    {
        var runtime = new PluginRuntime
        {
            Name = injectable.Callback.Method.Name,
            Callback = (ctx, config) => injectable.Callback(ctx, config),
        };
        foreach (var dep in injectable.Deps)
        {
            runtime.Inject[dep] = null;
        }
        return runtime;
    }

    private sealed class InjectablePlugin(IEnumerable<string> deps, Func<Context, object?, object?> callback)
        : IPluginObject
    {
        public IReadOnlyList<string> Deps { get; } = deps.ToList();
        public Func<Context, object?, object?> Callback { get; } = callback;
        public string Name => Callback.Method.Name;

        public object? Apply(Context ctx, object? config) => Callback(ctx, config);
    }
}