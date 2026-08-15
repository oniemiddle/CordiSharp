namespace CordiSharp.Events;

/// <summary>A named event key. Typed keys carry the payload type of the event.</summary>
public class EventKey
{
    public string Name { get; }

    protected EventKey(string name) => Name = name;

    /// <summary>Creates a typed event key.</summary>
    public static EventKey<TArgs> Create<TArgs>(string name) => new(name);

    public override string ToString() => Name;
}

/// <summary>A typed event key carrying a payload of <typeparamref name="TArgs"/>.</summary>
public sealed class EventKey<TArgs> : EventKey
{
    internal EventKey(string name) : base(name) { }
}
