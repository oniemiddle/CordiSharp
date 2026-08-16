namespace CordiSharp.Samples.LightTree;

/// <summary>A directed dependency edge: <see cref="From"/> depends on <see cref="To"/>
/// (From injects To's service). Rendered as an arrow from From toward To.</summary>
public sealed class EdgeViewModel
{
    public NodeViewModel From { get; }
    public NodeViewModel To { get; }

    public EdgeViewModel(NodeViewModel from, NodeViewModel to)
    {
        From = from;
        To = to;
    }
}
