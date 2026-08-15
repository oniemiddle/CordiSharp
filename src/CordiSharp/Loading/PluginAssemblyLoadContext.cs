using System.Reflection;
using System.Runtime.Loader;

namespace CordiSharp.Loading;

/// <summary>A collectible <see cref="AssemblyLoadContext"/> that hosts one external plugin
/// assembly. Shared assemblies (CordiSharp core, the BCL, host dependencies) resolve from
/// the default context so that both sides share the same type identities; the plugin's own
/// dependencies are probed from the plugin directory.</summary>
internal sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private readonly string _directory;

    public PluginAssemblyLoadContext(string name, string directory)
        : base(name, isCollectible: true)
    {
        _directory = directory;
        Resolving += OnResolving;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // shared / framework assemblies live in the default context; only the plugin's
        // unique dependencies fall through (return null) to the Resolving handler below
        try
        {
            return Default.LoadFromAssemblyName(assemblyName);
        }
        catch
        {
            return null;
        }
    }

    private Assembly? OnResolving(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        var path = Path.Combine(_directory, assemblyName.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }
}
