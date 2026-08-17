using CordiSharp.Registry;

namespace CordiSharp.Samples.Basic;

[Inject("greeter")]
public sealed class DependentPlugin : IPlugin<DependentConfig>
{
    public void Load(Context ctx, DependentConfig config)
    {
        var greeter = ctx.Get<GreeterService>("greeter")!;
        Console.WriteLine($"{config.Message}: {greeter.Greet("cordis")}");
    }
}