using CordiSharp.Schema;

namespace CordiSharp.Loading;

/// <summary>A plugin discovered in an external assembly. Metadata is resolved by reflection
/// at load time and is scoped to the owning <see cref="AssemblyPluginSet"/> — it is never
/// registered into the static <see cref="Registry.PluginMetadataRegistry"/>, so unloading
/// the assembly never leaves a rooted type behind.
/// <para>Do not retain this object (nor <see cref="ConfigType"/>) after the owning set has
/// been unloaded; doing so pins the assembly and prevents unload.</para></summary>
public sealed class AssemblyPluginInfo
{
    private IReadOnlyList<string> _injectNames;

    internal AssemblyPluginInfo(string name, Type type, Type? configType,
        Schema.Schema? configSchema, IReadOnlyList<string> injectNames)
    {
        Name = name;
        Type = type;
        ConfigType = configType;
        ConfigSchema = configSchema;
        _injectNames = injectNames;
    }

    /// <summary>The plugin name (from <c>[Plugin("name")]</c>, or the type name).</summary>
    public string Name { get; }

    /// <summary>The plugin implementation type, defined in the external assembly.</summary>
    internal Type? Type { get; private set; }

    /// <summary>The plugin's config type, defined in the external assembly. Retaining it
    /// after unload pins the assembly; use <see cref="ConfigSchema"/> to author configs.</summary>
    public Type? ConfigType { get; private set; }

    /// <summary>The config schema (validates/coerces config values; does not pin the assembly).</summary>
    public Schema.Schema? ConfigSchema { get; private set; }

    /// <summary>Names of required injected services declared on the plugin class.</summary>
    public IReadOnlyList<string> InjectNames => _injectNames;

    internal void Detach()
    {
        Type = null;
        ConfigType = null;
        ConfigSchema = null;
        _injectNames = [];
    }

    public override string ToString() => $"AssemblyPluginInfo <{Name}>";
}
