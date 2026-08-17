using CordiSharp;
using CordiSharp.Events;
using CordiSharp.Extensions.DependencyInjection;
using CordiSharp.Extensions.Hosting;
using CordiSharp.Extensions.Logging;
using CordiSharp.Samples.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static System.Console;

// =========================================================================
// CordiSharp + Microsoft.Extensions.Hosting
// The CordiSharpHost is an IHostedService: plugins load when the host
// starts and unload when it stops. Plugin classes can take MSDI services.
// The logging bridge (CordiSharp.Extensions.Logging) routes CordiSharp's
// ctx.Logger() output into the host's logging pipeline (console by default).
// =========================================================================

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddCordiSharp(o =>
{
    o.AddPlugin<NotifierPlugin>(new NotifierConfig { Prefix = "[notify]" });
    o.AddPlugin<GreeterPlugin>(new GreeterConfig { Name = "CordiSharp" });
});
builder.Services.AddCordiSharpHosting();
builder.Services.AddCordiSharpLogging();
builder.Services.AddSingleton<INotifier, ConsoleNotifier>();

using var host = builder.Build();

// Bridge CordiSharp -> Microsoft.Extensions.Logging: every ctx.Logger() message
// is forwarded to the host's ILoggerFactory. (The reverse direction - MEL ->
// CordiSharp - is available via builder.Logging.AddCordiSharpLogging().)
using var logBridge = host.Services.GetRequiredService<Context>()
    .UseLoggerFactory(host.Services.GetRequiredService<ILoggerFactory>());

await host.StartAsync();

WriteLine("\nHost is running. Press Enter to stop...");
ReadLine();

await host.StopAsync();
WriteLine("host stopped");

internal static class TestEvents
{
    public static readonly EventKey<int> Tick = EventKey.Create<int>("tick");
}