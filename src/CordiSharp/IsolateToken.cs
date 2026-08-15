namespace CordiSharp;

/// <summary>Identity token used for service isolation. Two scopes share a service
/// instance iff they resolve the same token for the service name.</summary>
public sealed class IsolateToken(string name)
{
    public string Name { get; } = name;
    public override string ToString() => $"Symbol({Name})";
}
