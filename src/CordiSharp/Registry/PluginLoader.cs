using System.Reflection;

namespace CordiSharp.Registry;

/// <summary>Instantiates and drives plugin classes (ctor + injects + lifecycle).</summary>
internal static class PluginLoader
{
    public static object? Load(Context ctx, Type type, object? config)
    {
        var instance = CreateInstance(ctx, type, config);
        ApplyInjectProperties(ctx, instance);

        // IPlugin<TConfig> / IAsyncPlugin<TConfig> are contravariant; dispatch via the
        // concrete interface implemented by the type (reflection)
        var pluginInterface = FindPluginInterface(type);
        if (pluginInterface is not null)
        {
            var isAsync = pluginInterface.GetGenericTypeDefinition() == typeof(IAsyncPlugin<>);
            var method = pluginInterface.GetMethod(isAsync ? "LoadAsync" : "Load")!;
            if (isAsync)
            {
                var task = (Task)method.Invoke(instance, [ctx, config])!;
                return LoadAsyncAndRegister(instance, task);
            }
            method.Invoke(instance, [ctx, config]);
            return RegisterDisposal(instance);
        }

        if (instance is Service service)
        {
            var init = service.RunInit();
            if (init is Task task) return AwaitInitAndDispose(instance, task);
            if (init is not null) return init;
            return RegisterDisposal(instance);
        }
        return RegisterDisposal(instance);
    }

    private static Type? FindPluginInterface(Type type)
    {
        foreach (var iface in type.GetInterfaces())
        {
            if (!iface.IsGenericType) continue;
            var def = iface.GetGenericTypeDefinition();
            if (def == typeof(IPlugin<>) || def == typeof(IAsyncPlugin<>))
            {
                return iface;
            }
        }
        return null;
    }

    private static async Task<object?> LoadAsyncAndRegister(object instance, Task loadTask)
    {
        await loadTask;
        return RegisterDisposal(instance);
    }

    private static async Task<object?> AwaitInitAndDispose(object instance, Task task)
    {
        await task;
        if (task is Task<object?> typed) return await typed;
        return RegisterDisposal(instance);
    }

    private static object? RegisterDisposal(object instance)
    {
        if (instance is IAsyncDisposable asyncDisposable)
        {
            return Disposer.From(() => asyncDisposable.DisposeAsync());
        }
        if (instance is IDisposable disposable)
        {
            return Disposer.From(disposable.Dispose);
        }
        return null;
    }

    private static object CreateInstance(Context ctx, Type type, object? config)
    {
        var provider = ctx.ServiceProvider;
        Exception? lastError = null;
        foreach (var ctor in type.GetConstructors().OrderByDescending(c => c.GetParameters().Length))
        {
            var parameters = ctor.GetParameters();
            var args = new object?[parameters.Length];
            var usable = true;
            for (var i = 0; i < parameters.Length; i++)
            {
                var paramType = parameters[i].ParameterType;
                if (paramType == typeof(Context))
                {
                    args[i] = ctx;
                }
                else if (config is not null && paramType.IsInstanceOfType(config))
                {
                    args[i] = config;
                }
                else if (provider is not null && (paramType.IsClass || paramType.IsInterface) && paramType != typeof(string))
                {
                    args[i] = provider.GetService(paramType);
                    if (args[i] is null) { usable = false; break; }
                }
                else if (paramType.IsValueType)
                {
                    // value-type param not satisfied by ctx/config: try provider
                    if (provider is not null)
                    {
                        args[i] = provider.GetService(paramType);
                        if (args[i] is null) { usable = false; break; }
                    }
                    else
                    {
                        usable = false;
                        break;
                    }
                }
                else
                {
                    usable = false;
                    break;
                }
            }
            if (!usable) continue;
            try
            {
                return ctor.Invoke(args);
            }
            catch (TargetInvocationException e)
            {
                lastError = e.InnerException ?? e;
            }
            catch (Exception e)
            {
                lastError = e;
            }
        }
        throw new CordisException($"cannot instantiate plugin {type.Name}: no suitable constructor", lastError);
    }

    private static void ApplyInjectProperties(Context ctx, object instance)
    {
        foreach (var prop in instance.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var attr = prop.GetCustomAttribute<InjectAttribute>(inherit: true);
            if (attr is null || !prop.CanWrite) continue;
            var value = ctx.Get(attr.Name);
            if (value is not null && prop.PropertyType.IsInstanceOfType(value))
            {
                prop.SetValue(instance, value);
            }
        }
    }
}