using CordiSharp.Registry;

namespace CordiSharp.Extensions.DependencyInjection;

/// <summary>Options for configuring CordiSharp inside Microsoft.Extensions.DependencyInjection.</summary>
public sealed class CordiSharpOptions
{
    internal List<(Type PluginType, object? Config)> Plugins { get; } = [];

    /// <summary>Registers a plugin to be loaded when the host starts.</summary>
    public CordiSharpOptions AddPlugin(Type pluginType, object? config = null)
    {
        Plugins.Add((pluginType, config));
        return this;
    }

    /// <summary>Registers a typed plugin to be loaded when the host starts.</summary>
    public CordiSharpOptions AddPlugin<T>(object? config = null) where T : class, IPlugin, new()
    {
        Plugins.Add((typeof(T), config));
        return this;
    }
}
