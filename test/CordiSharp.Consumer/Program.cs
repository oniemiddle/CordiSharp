using CordiSharp;
using CordiSharp.Extensions.DependencyInjection;
using CordiSharp.Extensions.Hosting;
using CordiSharp.Schema;
using Microsoft.Extensions.DependencyInjection;

// 1. Generated metadata: the [Plugin] class below is discovered by the source generator
var root = Context.Create();
var handle = root.Plugin(typeof(GreeterPlugin), new GreeterConfig { Message = "hello from consumer" });
await handle.Await();
Console.WriteLine("plugin loaded, ctx name = " + handle.Ctx.Name);
Console.WriteLine("greetings: " + string.Join(",", GreeterPlugin.Messages));

// 2. Events + effects
var count = 0;
var evt = EventKey.Create<object?>("ping");
var d = root.On(evt, (ctx, args) => { count++; return null; });
root.Emit(evt, null);
Console.WriteLine("event count: " + count);
d.Dispose();

// 3. MSDI integration
var services = new ServiceCollection();
services.AddCordiSharp(o => o.AddPlugin(typeof(GreeterPlugin), new GreeterConfig { Message = "via msdi" }));
services.AddCordiSharpHosting();
services.AddSingleton<IGreeter, ConsoleGreeter>();
await using var provider = services.BuildServiceProvider();
var host = provider.GetRequiredService<CordiSharpHost>();
await host.StartAsync();
Console.WriteLine("msdi greetings: " + string.Join(",", GreeterPlugin.Messages));
await host.StopAsync();

Console.WriteLine("CONSUMER OK");

[Plugin("greeter")]
public sealed class GreeterPlugin : IPlugin<GreeterConfig>
{
    public static List<string> Messages { get; } = new();
    private readonly IGreeter? _greeter;
    public GreeterPlugin() { }
    public GreeterPlugin(IGreeter greeter) => _greeter = greeter;
    public void Load(Context ctx, GreeterConfig config)
    {
        Messages.Add(config.Message);
        _greeter?.Say(config.Message);
    }
}

[PluginConfig]
public sealed class GreeterConfig
{
    public string? Message { get; set; }
}

public interface IGreeter { void Say(string message); }
public sealed class ConsoleGreeter : IGreeter { public void Say(string message) => Console.WriteLine("  [greeter] " + message); }
