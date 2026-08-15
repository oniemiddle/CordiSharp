using CordiSharp.Registry;
using CordiSharp.Schema;

namespace CordiSharp.Samples.PluginLibrary;

/// <summary>A service defined in the plugin library.</summary>
[Service("greeter")]
public sealed class GreeterService(Context ctx) : Service(ctx)
{
    public string Greet(string name) => $"Hello, {name}!";
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