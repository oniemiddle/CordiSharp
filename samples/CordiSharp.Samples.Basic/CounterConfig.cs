using CordiSharp.Schema;

namespace CordiSharp.Samples.Basic;

[PluginConfig]
public sealed class CounterConfig
{
    [DefaultValue(0)]
    public int Start { get; set; }
}