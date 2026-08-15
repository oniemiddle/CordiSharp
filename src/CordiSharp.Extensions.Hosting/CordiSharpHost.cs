using CordiSharp.Extensions.DependencyInjection;
using CordiSharp.Registry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CordiSharp.Extensions.Hosting;

/// <summary>Hosts a CordiSharp root context inside an MSDI application: loads configured
/// plugins on <c>StartAsync</c> and unloads them on <c>StopAsync</c>.</summary>
public sealed class CordiSharpHost(Context rootContext, IOptions<CordiSharpOptions> options)
    : IHostedService, IDisposable
{
    private readonly CordiSharpOptions _options = options.Value;
    private readonly List<PluginHandle> _plugins = [];

    /// <summary>The root context this host manages.</summary>
    public Context RootContext { get; } = rootContext;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        foreach (var (pluginType, config) in _options.Plugins)
        {
            var handle = RootContext.Plugin(pluginType, config);
            _plugins.Add(handle);
            await handle.Await();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        foreach (var handle in _plugins.AsEnumerable().Reverse().ToList())
        {
            await handle.DisposeAsync();
        }
        _plugins.Clear();
    }

    public void Dispose() => _ = StopAsync();
}
