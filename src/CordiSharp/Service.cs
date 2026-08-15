using CordiSharp.Events;
using CordiSharp.Registry;

namespace CordiSharp;

/// <summary>Base class for cordis services. Subclasses provide themselves as a service
/// named after the class (or <see cref="ServiceAttribute"/>). Ports cordis <c>Service</c>.</summary>
public abstract class Service : IContextFilter
{
    /// <summary>The context this service was created in (usually the plugin's context).</summary>
    public Context Ctx { get; }

    /// <summary>The service name.</summary>
    public string Name { get; }

    protected Service(Context ctx, string? name = null)
    {
        Ctx = ctx;
        Name = name ?? GetServiceName(GetType());
        ctx.Provide(Name, this, Check);
    }

    /// <summary>Optional availability check for the service.</summary>
    protected virtual bool Check() => true;

    /// <summary>Lifecycle hook called after construction; may return a disposer
    /// (or a <see cref="Task"/> of one). Mirrors cordis <c>Service.init</c>.</summary>
    protected virtual object? Init() => null;

    internal object? RunInit() => Init();

    /// <summary>Filter used when this service acts as an event dispatch thisArg:
    /// only hooks registered in the same isolate scope for this service name run.</summary>
    public virtual bool FilterContext(Context target)
    {
        target.Isolates.TryGet(Name, out var a);
        Ctx.Isolates.TryGet(Name, out var b);
        return ReferenceEquals(a, b);
    }

    private static string GetServiceName(Type type)
    {
        var attr = type.GetCustomAttributes(typeof(ServiceAttribute), inherit: true).FirstOrDefault() as ServiceAttribute;
        return attr?.Name ?? type.Name;
    }
}