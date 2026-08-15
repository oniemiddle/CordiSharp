namespace CordiSharp.Events;

/// <summary>Options for registering an event listener.</summary>
public sealed class EventOptions
{
    /// <summary>Insert the listener at the head of the hook list (runs before others).</summary>
    public bool Prepend { get; set; }

    /// <summary>Global listeners are never filtered by the dispatch thisArg.</summary>
    public bool Global { get; set; }

    public static implicit operator EventOptions(bool prepend) => new() { Prepend = prepend };
}
