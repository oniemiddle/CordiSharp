using System.Reflection;

namespace CordiSharp.Registry;

/// <summary>Compile-time metadata for a plugin class, produced by the source generator
/// (CordiSharp.Generators). The runtime prefers this over reflection.</summary>
public sealed class PluginMetadata(
    string? name,
    Type? configType,
    Schema.Schema? configSchema,
    IReadOnlyList<KeyValuePair<string, object?>>? inject = null)
{
    public string? Name { get; } = name;
    public Type? ConfigType { get; } = configType;
    public Schema.Schema? ConfigSchema { get; } = configSchema;
    public IReadOnlyList<KeyValuePair<string, object?>> Inject { get; } = inject ?? [];
}

/// <summary>Registry of compile-time plugin metadata produced by the source generator.</summary>
public static class PluginMetadataRegistry
{
    private static readonly Dictionary<Type, PluginMetadata> Registry = new();
    private static readonly object Gate = new();

    public static void Register(Type pluginType, PluginMetadata metadata)
    {
        lock (Gate)
        {
            Registry[pluginType] = metadata;
        }
    }

    internal static PluginMetadata? Get(Type pluginType)
    {
        lock (Gate)
        {
            return Registry.TryGetValue(pluginType, out var metadata) ? metadata : null;
        }
    }

    internal static void EnsureGeneratedRegistrations()
    {
        lock (Gate)
        {
            if (Initialized) return;
            Initialized = true;
        }
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType("CordiSharp.Generated.PluginRegistrations");
            if (type is null) continue;
            var method = type.GetMethod("RegisterAll", BindingFlags.Public | BindingFlags.Static);
            method?.Invoke(null, null);
        }
    }

    private static bool Initialized;
}
