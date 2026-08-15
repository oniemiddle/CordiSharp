using CordiSharp.Registry;
using CordiSharp.Schema;

namespace CordiSharp.Samples.PluginLibrary;

/// <summary>Provides the "counter" service. Lives in the plugin library assembly.</summary>
[Plugin("counter")]
public sealed class CounterPlugin : IPlugin<CounterConfig>
{
    public void Load(Context ctx, CounterConfig config)
    {
        ctx.Provide("counter", config.Start);
        ctx.Effect(() =>
        {
            Console.WriteLine($"[library] counter plugin loaded (start = {config.Start})");
            return () => Console.WriteLine("[library] counter plugin disposed");
        }, "counter-body");
    }
}

[PluginConfig]
public sealed class CounterConfig
{
    [DefaultValue(0)]
    public int Start { get; set; }
}
