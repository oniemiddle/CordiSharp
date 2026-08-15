using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CordiSharp.Extensions.DependencyInjection;

/// <summary>MSDI wiring for CordiSharp.</summary>
public static class CordiSharpServiceCollectionExtensions
{
    /// <summary>Registers CordiSharp services: a root <see cref="Context"/> (singleton)
    /// with the MSDI provider attached for plugin construction. To also load the
    /// configured plugins on host start, call <c>AddCordiSharpHosting()</c> (available in
    /// the CordiSharp.Extensions.Hosting package).</summary>
    public static IServiceCollection AddCordiSharp(this IServiceCollection services, Action<CordiSharpOptions>? configure = null)
    {
        services.AddOptions<CordiSharpOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }
        services.TryAddSingleton(sp =>
        {
            var ctx = Context.Create();
            ctx.ServiceProvider = sp;
            return ctx;
        });
        return services;
    }

    /// <summary>Attaches an MSDI provider to an existing root context (for plugin construction).</summary>
    public static Context UseServiceProvider(this Context ctx, IServiceProvider provider)
    {
        ctx.ServiceProvider = provider;
        return ctx;
    }
}

/// <summary>Resolves CLR services from the attached MSDI provider.</summary>
public static class ContextServiceProviderExtensions
{
    extension(Context ctx)
    {
        public T? Resolve<T>() where T : class
            => ctx.ServiceProvider?.GetService<T>();

        public object? Resolve(Type type)
            => ctx.ServiceProvider?.GetService(type);
    }
}
