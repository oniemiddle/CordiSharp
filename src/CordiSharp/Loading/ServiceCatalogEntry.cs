namespace CordiSharp.Loading;

/// <summary>One entry of the generated plugin service catalog: a contract interface
/// (declared outside the plugin assembly) mapped to the plugin's internal service type and
/// its service name. Produced by the CordiSharp service-catalog source generator.
/// <para><see cref="Impl"/> (and the whole entry) lives in the external assembly: do not
/// retain it after the owning <see cref="AssemblyPluginSet"/> is unloaded.</para></summary>
public sealed record ServiceCatalogEntry(Type Contract, string ServiceName, Type Impl);
