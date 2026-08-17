namespace CordiSharp.Samples.LightTree;

/// <summary>A directed dependency edge: <see cref="From"/> depends on <see cref="To"/>
/// (From injects To's service). Rendered as an arrow from From toward To.</summary>
public sealed class EdgeViewModel(NodeViewModel from, NodeViewModel to)
{
    public NodeViewModel From { get; } = from;
    public NodeViewModel To { get; } = to;
}
