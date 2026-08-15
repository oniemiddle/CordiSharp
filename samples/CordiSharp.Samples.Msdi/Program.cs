using CordiSharp.Events;
using CordiSharp.Extensions.DependencyInjection;
using CordiSharp.Extensions.Hosting;
using CordiSharp.Registry;
using CordiSharp.Samples.Msdi;
using CordiSharp.Schema;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// =========================================================================
// CordiSharp + Microsoft.Extensions.Hosting
// The CordiSharpHost is an IHostedService: plugins load when the host
// starts and unload when it stops. Plugin classes can take MSDI services.
// =========================================================================

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddCordiSharp(o =>
{
    o.AddPlugin(typeof(NotifierPlugin), new NotifierConfig { Prefix = "[notify]" });
    o.AddPlugin(typeof(GreeterPlugin), new GreeterConfig { Name = "CordiSharp" });
});
builder.Services.AddCordiSharpHosting();
builder.Services.AddSingleton<INotifier, ConsoleNotifier>();

using var host = builder.Build();
await host.StartAsync();

Console.WriteLine("\nHost is running. Press Enter to stop...");
Console.ReadLine();

await host.StopAsync();
Console.WriteLine("host stopped");

namespace CordiSharp.Samples.Msdi
{
    // ============================ plugin definitions ===========================

    [Plugin("notifier")]
    public sealed class NotifierPlugin(INotifier notifier) : IPlugin<NotifierConfig>
    {
        // resolved from MSDI

        public void Load(Context ctx, NotifierConfig config)
        {
            ctx.On(TestEvents.Tick, (_, tick) =>
            {
                notifier.Notify($"{config.Prefix} tick {tick}");
                return null;
            });
            Console.WriteLine("notifier plugin loaded (waits for ticks)");
        }
    }

    [PluginConfig]
    public sealed class NotifierConfig
    {
        public string? Prefix { get; set; }
    }

    [Plugin("greeter")]
    public sealed class GreeterPlugin(INotifier notifier) : IPlugin<GreeterConfig>
    {
        private readonly INotifier _notifier = notifier;
        private Context _ctx = null!;
        private GreeterConfig _config = null!;

        public void Load(Context ctx, GreeterConfig config)
        {
            _ctx = ctx;
            _config = config;
            // emit a few ticks so the notifier plugin reacts
            ctx.Effect(() =>
            {
                var timer = new Timer(_ => ctx.Emit(TestEvents.Tick, Random.Shared.Next(100)), null, 0, 1000);
                Console.WriteLine($"greeter plugin loaded (name = {config.Name})");
                return () => { timer.Dispose(); Console.WriteLine("greeter plugin disposed"); };
            }, "ticker");
        }
    }

    [PluginConfig]
    public sealed class GreeterConfig
    {
        public string? Name { get; set; }
    }

    public static class TestEvents
    {
        public static readonly EventKey<int> Tick = EventKey.Create<int>("tick");
    }

    public interface INotifier { void Notify(string message); }

    public sealed class ConsoleNotifier : INotifier
    {
        public void Notify(string message) => Console.WriteLine($"  {message}");
    }
}