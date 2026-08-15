namespace CordiSharp.Loading;

/// <summary>Thrown when a service bridge (<see cref="AssemblyPluginSet.GetService{T}"/>)
/// belonging to an unloaded assembly is used after the assembly was unloaded.</summary>
public sealed class PluginUnloadedException(string message) : CordisException(message);
