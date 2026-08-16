namespace CordiSharp.Loading;

/// <summary>Runtime helper used by generated <c>[Import]</c> accessors: resolves the raw
/// plugin service from the context and wraps it in the generated weak bridge (which holds
/// only a <see cref="WeakReference"/> to the plugin instance and throws
/// <see cref="PluginUnloadedException"/> after unload).</summary>
public static class ImportResolver
{
    public static T Resolve<T, TBridge>(Context ctx, string serviceName) where TBridge : class
    {
        var raw = ctx.Root.Get(serviceName, strict: false)
            ?? throw new PluginUnloadedException(
                $"""imported service "{serviceName}" is not available (no loaded plugin provides it, or it was unloaded)""");
        try
        {
            return (T)(object)Activator.CreateInstance(typeof(TBridge), raw, serviceName, ctx)!;
        }
        catch (MissingMethodException error)
        {
            throw new CordisException($"generated bridge {typeof(TBridge).Name} must expose an (object, string, Context) constructor", error);
        }
    }

    /// <summary>Resolves an injected service through the fiber chain (isolate-aware) and
    /// wraps it in the generated weak bridge. Used by accessors generated from
    /// <c>[Inject(name, Alias)]</c>.</summary>
    public static T ResolveLocal<T, TBridge>(Context ctx, string serviceName) where TBridge : class
    {
        var raw = ctx.Get(serviceName, strict: false)
            ?? throw new PluginUnloadedException(
                $"""injected service "{serviceName}" is not available in this context (plugin unloaded or never provided)""");
        try
        {
            return (T)(object)Activator.CreateInstance(typeof(TBridge), raw, serviceName, ctx)!;
        }
        catch (MissingMethodException error)
        {
            throw new CordisException($"generated bridge {typeof(TBridge).Name} must expose an (object, string, Context) constructor", error);
        }
    }
}
