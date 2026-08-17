using CordiSharp.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CordiSharp.Extensions.Hosting;

/// <summary>Hosting wiring for CordiSharp.</summary>
public static class CordiSharpHostingServiceCollectionExtensions
{
    /// <summary>Registers CordiSharp services (root <see cref="Context"/> singleton, options)
    /// and a <see cref="CordiSharpHost"/> that loads the configured plugins on start and
    /// unloads them on stop. The host is also registered as an <see cref="IHostedService"/>,
    /// so it runs automatically with <c>Microsoft.Extensions.Hosting</c>.</summary>
    public static IServiceCollection AddCordiSharpHosting(this IServiceCollection services, Action<CordiSharpOptions>? configure = null)
    {
        return services
            .AddCordiSharp(configure)
            .AddSingleton<IHostedService, CordiSharpHost>();
    }
}
