using CordiSharp.Registry;
using CordiSharp.Schema;

namespace CordiSharp.Samples.PluginLibrary;

/// <summary>A service defined in the plugin library.</summary>
[Service("greeter")]
public sealed class GreeterService(Context ctx) : Service(ctx)
{
    public string Greet(string name) => $"Hello, {name}!";
}

/// <summary>Another service defined in the plugin library (used to demonstrate multiple
/// <c>[Import]</c> annotations on one host type).</summary>
[Service("echo")]
public sealed class EchoService(Context ctx) : Service(ctx)
{
    public string Echo(string text) => text;
}

/// <summary>A service used to demonstrate <c>[Inject(name, Alias)]</c> accessors.</summary>
[Service("ping")]
public sealed class PingService(Context ctx) : Service(ctx)
{
    public string Ping() => "pong";
}

/// <summary>A plugin in the library that injects the library's greeter service.</summary>
[Inject("greeter")]
public sealed class DependentPlugin : IPlugin<DependentConfig>
{
    public void Load(Context ctx, DependentConfig config)
    {
        var greeter = ctx.Get<GreeterService>("greeter")!;
        Console.WriteLine($"[library] {config.Message}: {greeter.Greet("cordis")}");
    }
}

[PluginConfig]
public sealed class DependentConfig
{
    public string? Message { get; set; }
}