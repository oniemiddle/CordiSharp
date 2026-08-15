namespace CordiSharp.Registry;

/// <summary>Marker interface for plugin classes.</summary>
public interface IPlugin
{
}

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

/// <summary>Declares a required service injection on a plugin class or property.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = true)]
public sealed class InjectAttribute(string name, object? config) : Attribute
{
    public string Name { get; } = name;
    public object? Config { get; } = config;

    public InjectAttribute(string name) : this(name, null)
    {
    }
}

/// <summary>Declares the service name of a <see cref="Service"/> subclass.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ServiceAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
