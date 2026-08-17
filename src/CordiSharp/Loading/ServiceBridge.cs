using System.Reflection;

namespace CordiSharp.Loading;

/// <summary>Base class for service bridges (non-generic bookkeeping so a set can revoke
/// bridges of any contract type). Infrastructure type — not intended for direct use.</summary>
public abstract class ServiceBridgeBase : DispatchProxy
{
    public abstract void Revoke();
}

/// <summary>A weak-reference bridge that adapts a plugin-provided service to a host-defined
/// interface contract. The bridge holds only a <see cref="WeakReference"/> to the plugin
/// service, so retaining it never prevents assembly unload; once the owning assembly is
/// unloaded (or the bridge is revoked) every invocation throws
/// <see cref="PluginUnloadedException"/>. Calls are forwarded to the plugin service by
/// method name and arity — the plugin type does not have to implement the contract (and
/// must not: a compile-time reference would pin the assembly).</summary>
/// <summary>Infrastructure type — not intended for direct use. Must be public and
/// unsealed for <see cref="DispatchProxy.Create{T,TProxy}"/>.</summary>
public class ServiceBridge<T> : ServiceBridgeBase where T : class
{
    private WeakReference<object>? _target;
    private string _serviceName = "";
    private bool _revoked;

    public static T Create(object target, string serviceName)
    {
        var proxy = DispatchProxy.Create<T, ServiceBridge<T>>();
        var bridge = (ServiceBridge<T>)(object)proxy;
        bridge._target = new WeakReference<object>(target);
        bridge._serviceName = serviceName;
        return (T)(object)bridge;
    }

    public override void Revoke() => _revoked = true;

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (_revoked || _target is not { } weak || !weak.TryGetTarget(out var target))
        {
            throw new PluginUnloadedException(
                $"""service "{_serviceName}" belongs to an unloaded assembly; the bridge is no longer usable""");
        }

        var name = method?.Name ?? throw new ArgumentNullException(nameof(method));
        var argCount = args?.Length ?? 0;
        var candidate = target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
        if (candidate is null || candidate.GetParameters().Length != argCount)
        {
            throw new CordisException(
                $"""service "{_serviceName}" (type {target.GetType().Name}) does not expose a method""" +
                $""" "{name}" with {argCount} parameter(s) as required by contract {typeof(T).Name}""");
        }
        try
        {
            return candidate.Invoke(target, args);
        }
        catch (TargetInvocationException error)
        {
            throw error.InnerException ?? error;
        }
        catch (ArgumentException error)
        {
            throw new CordisException(
                $"""cannot forward {typeof(T).Name}.{name} to service "{_serviceName}": {error.Message}""", error);
        }
    }
}
