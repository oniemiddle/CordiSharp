namespace CordiSharp.Loading;

/// <summary>Thrown when an external assembly cannot be unloaded because strong references
/// to its types are still held (see <see cref="AssemblyLoaderService.UnloadAsync"/>).</summary>
public sealed class AssemblyUnloadException : CordisException
{
    public AssemblyUnloadException(string message, Exception inner) : base(message, inner) { }
    
    public AssemblyUnloadException(string message) : base(message) { }
}
