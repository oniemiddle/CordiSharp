namespace CordiSharp.Registry;

/// <summary>Declares an imported plugin service at the ASSEMBLY level (host entry point).
/// The CordiSharp import source generator finds the implementation type in a referenced
/// plugin library (a <c>[Service]</c> subclass with this name), then generates in the host
/// assembly a mirrored interface, a weak bridge and a <c>ctx.&lt;Name&gt;</c> accessor
/// (C# 14 extension property). Usage: <c>[assembly: Import("greeter", Alias = "Greeter")]</c>.
/// Resolution goes through the root context (host perspective, ignores isolates);
/// plugins should use <c>[Inject(name, Alias)]</c> instead for isolate-aware access.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class ImportAttribute(string name) : Attribute
{
    /// <summary>The imported service name (must match a <c>[Service(name)]</c> in a
    /// referenced plugin library). Also becomes the generated <c>ctx.&lt;Name&gt;</c> accessor
    /// unless <see cref="Alias"/> is set.</summary>
    public string Name { get; } = name;

    /// <summary>Optional alias for the generated <c>ctx.&lt;Alias&gt;</c> extension property.
    /// Only affects the property name; the service is still resolved by <see cref="Name"/>.</summary>
    public string? Alias { get; set; }
}
