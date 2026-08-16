namespace CordiSharp.Registry;

/// <summary>Marker interface for plugin classes.</summary>
public interface IPlugin;

/// <summary>A plugin class with a synchronous load method.</summary>
public interface IPlugin<in TConfig> : IPlugin
{
    void Load(Context ctx, TConfig config);
}

/// <summary>A plugin class with an asynchronous load method.</summary>
public interface IAsyncPlugin<in TConfig> : IPlugin
{
    Task LoadAsync(Context ctx, TConfig config);
}

/// <summary>Object-style plugin with an apply method (mirrors cordis Plugin.Object).</summary>
public interface IPluginObject
{
    string? Name { get; }
    object? Apply(Context ctx, object? config);
}

/// <summary>Declares a plugin class and its metadata.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PluginAttribute : Attribute
{
    public string? Name { get; }
    public Type? ConfigType { get; set; }

    public PluginAttribute() { }
    public PluginAttribute(string name) => Name = name;
}

/// <summary>Declares a required service injection on a plugin class or property. When
/// <see cref="Alias"/> is set (class-level only), the import source generator additionally
/// emits a type-safe, isolate-aware <c>ctx.&lt;Alias&gt;</c> accessor (mirrored interface +
/// weak bridge) — "injected, therefore importable".</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = true)]
public sealed class InjectAttribute(string name, object? config = null) : Attribute
{
    public string Name { get; } = name;
    public object? Config { get; } = config;

    /// <summary>Optional alias: when set on a class-level <c>[Inject]</c>, the generator
    /// creates a <c>ctx.&lt;Alias&gt;</c> accessor for this injected service.</summary>
    public string? Alias { get; set; }
}

/// <summary>Declares the service name of a <see cref="Service"/> subclass.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ServiceAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
