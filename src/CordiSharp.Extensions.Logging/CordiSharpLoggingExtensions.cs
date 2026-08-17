using CordiSharp.Extensions.DependencyInjection;
using CordiSharp.Logger;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CordiSharp.Extensions.Logging;

/// <summary>Microsoft.Extensions.Logging wiring for CordiSharp.</summary>
public static class CordiSharpLoggingExtensions
{
    /// <summary>Registers CordiSharp logging on an <see cref="ILoggingBuilder"/>: MEL
    /// <see cref="ILogger{T}"/> entries are written into the root context's
    /// <see cref="LoggerService"/> (the MEL category becomes the CordiSharp logger name).
    /// Requires <c>AddCordiSharp()</c>, which is called implicitly.</summary>
    public static ILoggingBuilder AddCordiSharpLogging(this ILoggingBuilder builder)
    {
        builder.Services.AddCordiSharpLogging();
        return builder;
    }

    /// <summary>Registers CordiSharp services plus an <see cref="ILoggerProvider"/>
    /// (<see cref="CordiSharpLoggerProvider"/>) that forwards MEL log entries into the
    /// root context's <see cref="LoggerService"/>. Use together with <c>AddLogging()</c>
    /// (<c>Host.CreateApplicationBuilder</c> sets it up by default).</summary>
    public static IServiceCollection AddCordiSharpLogging(this IServiceCollection services)
    {
        services.AddCordiSharp();
        services.TryAddSingleton(sp => sp.GetRequiredService<Context>().LoggerService);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, CordiSharpLoggerProvider>());
        return services;
    }

    /// <summary>Bridges the root context's logger into a Microsoft.Extensions.Logging
    /// <see cref="ILoggerFactory"/>: every CordiSharp <see cref="LogMessage"/> is forwarded
    /// to a logger with the given category (default <c>CordiSharp</c>). The returned handle
    /// detaches the exporter when disposed. Safe to combine with
    /// <c>AddCordiSharpLogging()</c> — re-exported messages are not echoed back.</summary>
    public static IDisposable UseLoggerFactory(this Context ctx, ILoggerFactory factory, string? categoryName = null)
        => ctx.LoggerService.Exporter(new CordiSharpLogExporter(factory, categoryName));
}
