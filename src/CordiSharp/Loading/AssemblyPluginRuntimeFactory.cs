using System.Globalization;
using System.Reflection;
using CordiSharp.Registry;

namespace CordiSharp.Loading;

/// <summary>Builds <see cref="PluginRuntime"/> instances for plugin types loaded from a
/// collectible assembly. Uses reflection only: generated metadata is deliberately not
/// consulted, so the static <see cref="PluginMetadataRegistry"/> never roots the assembly's
/// types (which would make the load context un-unloadable).</summary>
internal static class AssemblyPluginRuntimeFactory
{
    public static PluginRuntime Create(Type type)
    {
        var attr = type.GetCustomAttributes(typeof(PluginAttribute), inherit: false).FirstOrDefault() as PluginAttribute;
        var name = attr?.Name ?? type.Name;
        var configType = attr?.ConfigType ?? FindConfigType(type);
        var runtime = new PluginRuntime
        {
            Name = name,
            PluginType = type,
            ConfigSchema = configType is not null ? Schema.Schema.FromType(configType) : null,
            Callback = (ctx, config) => PluginLoader.Load(ctx, type, config),
        };
        foreach (var inject in type.GetCustomAttributes(typeof(InjectAttribute), inherit: true).Cast<InjectAttribute>())
        {
            runtime.Inject[inject.Name] = inject.Config;
        }
        return runtime;
    }

    /// <summary>Coerces a user-supplied config into an instance of the plugin's config type
    /// (defined in the external assembly). Accepts an existing instance, a dictionary or a
    /// host-side POCO with matching properties; validates through the config schema first.</summary>
    public static object? MaterializeConfig(Type? configType, Schema.Schema? schema, object? config)
    {
        if (config is null || configType is null || configType.IsInstanceOfType(config)) return config;

        var validated = schema is null ? config : schema.Parse(config);
        if (configType.IsInstanceOfType(validated)) return validated;

        object? instance;
        try
        {
            instance = Activator.CreateInstance(configType);
        }
        catch (Exception)
        {
            // no parameterless ctor: fall through — the loader will fail later with a
            // clear instantiation error if the plugin actually needs the typed config
            return validated;
        }
        if (instance is null) return validated;

        var sourceDict = validated as IDictionary<string, object?>;
        foreach (var prop in configType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite || prop.SetMethod is null) continue;
            object? value;
            if (sourceDict is not null)
            {
                sourceDict.TryGetValue(prop.Name, out value);
            }
            else
            {
                var sourceProp = validated?.GetType().GetProperty(prop.Name);
                value = sourceProp is { CanRead: true } ? sourceProp.GetValue(validated) : null;
            }
            if (value is null) continue;
            // the schema coerces scalars (e.g. Integer -> long); convert to the target
            // property type when assigning onto the plugin's own config class
            var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            if (!targetType.IsInstanceOfType(value))
            {
                try
                {
                    value = Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
                }
                catch (Exception)
                {
                    continue; // unconvertible: leave the default value
                }
            }
            prop.SetValue(instance, value);
        }
        return instance;
    }

    private static Type? FindConfigType(Type type)
    {
        foreach (var iface in type.GetInterfaces())
        {
            if (!iface.IsGenericType) continue;
            var def = iface.GetGenericTypeDefinition();
            if (def == typeof(IPlugin<>) || def == typeof(IAsyncPlugin<>))
            {
                return iface.GetGenericArguments()[0];
            }
        }
        return null;
    }
}
