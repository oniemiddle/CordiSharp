namespace CordiSharp.Registry;

/// <summary>Declares an imported plugin service on the host. The CordiSharp import source
/// generator finds the implementation type in a referenced plugin library (a <c>[Service]</c>
/// subclass with this name), then generates a mirrored interface, a weak bridge and a
/// <c>ctx.&lt;Name&gt;</c> accessor (C# 14 extension property) in the host assembly.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ImportAttribute(string name) : Attribute
{
    /// <summary>The imported service name (must match a <c>[Service(name)]</c> in a
    /// referenced plugin library). Also becomes the generated <c>ctx.&lt;Name&gt;</c> accessor.</summary>
    public string Name { get; } = name;
}
