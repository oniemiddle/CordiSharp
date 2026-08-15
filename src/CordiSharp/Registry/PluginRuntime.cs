using CordiSharp.Internal;

namespace CordiSharp.Registry;

/// <summary>Metadata + invocation for one plugin type/delegate/object. Shared by all
/// fiber instances of the same plugin (mirrors cordis Plugin.Runtime).</summary>
public sealed class PluginRuntime
{
    internal string? Name;
    internal Type? PluginType;
    internal Schema.Schema? ConfigSchema;
    internal readonly Dictionary<string, object?> Inject = new();
    internal Func<Context, object?, object?> Callback = null!;
    internal readonly DisposableList<Fiber> Fibers = new();

    public string? PluginName => Name;
    public Type? Type => PluginType;
    public IReadOnlyList<string> InjectNames => Inject.Keys.ToList();
}