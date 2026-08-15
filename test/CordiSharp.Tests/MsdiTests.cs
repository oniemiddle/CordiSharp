using CordiSharp.Extensions.DependencyInjection;
using CordiSharp.Extensions.Hosting;
using CordiSharp.Registry;
using CordiSharp.Schema;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CordiSharp.Tests;

public class MsdiTests
{
    [Fact]
    public async Task Host_StartsAndStopsPlugins()
    {
        GreeterPlugin.Messages.Clear();
        var services = new ServiceCollection();
        services.AddCordiSharp(o => o.AddPlugin(typeof(GreeterPlugin), new GreeterConfig { Message = "hi" }));
        services.AddCordiSharpHosting();
        services.AddSingleton<IGreeter, ConsoleGreeter>();
        await using var provider = services.BuildServiceProvider();

        var host = provider.GetRequiredService<CordiSharpHost>();
        var ctx = provider.GetRequiredService<Context>();
        Assert.NotNull(ctx.ServiceProvider);

        await host.StartAsync();
        Assert.Equal(new[] { "hi" }, GreeterPlugin.Messages);

        await host.StopAsync();
        Assert.Equal(1, GreeterPlugin.Disposed);
    }

    [Fact]
    public void Context_ResolvesFromProvider()
    {
        var services = new ServiceCollection();
        services.AddCordiSharp();
        services.AddSingleton<IGreeter, ConsoleGreeter>();
        using var provider = services.BuildServiceProvider();
        var ctx = provider.GetRequiredService<Context>();
        var greeter = ctx.Resolve<IGreeter>();
        Assert.NotNull(greeter);
    }

    public interface IGreeter
    {
        void Say(string message);
    }

    public sealed class ConsoleGreeter : IGreeter
    {
        public void Say(string message) { }
    }

    [Plugin("greeter")]
    public sealed class GreeterPlugin(IGreeter greeter) : IPlugin<GreeterConfig>
    {
        public static List<string> Messages { get; } = [];
        public static int Disposed;

        public void Load(Context ctx, GreeterConfig config)
        {
            Messages.Add(config.Message);
            ctx.Effect(() =>
            {
                var self = this;
                return () => { Disposed++; };
            }, "dispose");
        }
    }

    [PluginConfig]
    public sealed class GreeterConfig
    {
        public string? Message { get; set; }
    }
}