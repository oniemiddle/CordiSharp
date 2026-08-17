using CordiSharp.Registry;

namespace CordiSharp.Samples.Basic;

[Plugin("counter")]
public sealed class CounterPlugin : IPlugin<CounterConfig>
{
    public void Load(Context ctx, CounterConfig config)
    {
        ctx.Provide("counter", config.Start);
        ctx.Effect(() =>
        {
            Console.WriteLine($"counter plugin loaded (start = {config.Start})");
            return () => Console.WriteLine("counter plugin disposed");
        }, "counter-body");
    }
}