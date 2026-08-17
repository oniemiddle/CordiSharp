namespace CordiSharp.Events;

/// <summary>A named event key. Typed keys carry the payload type of the event.</summary>
public record EventKey(string Name)
{
    /// <summary>Creates a typed event key.</summary>
    public static EventKey<TArgs> Create<TArgs>(string name) => new(name);

    public override string ToString() => Name;
}

/// <summary>A typed event key carrying a payload of <typeparamref name="TArgs"/>.</summary>
public sealed record EventKey<TArgs>(string Name) : EventKey(Name);
