using Avalonia.Media;
using CordiSharp.Registry;

namespace CordiSharp.Samples.LightTree;

/// <summary>One light in the tree: a visual node backed by a real CordiSharp plugin fiber.</summary>
public sealed class NodeViewModel(int id, double x, double y) : ObservableObject
{
    public const double Radius = 24;

    public int Id { get; } = id;
    public string Name { get; } = $"N{id}";

    // ---- position (world coordinates, node center) ----

    public double X
    {
        get;
        set => Set(ref field, value);
    } = x;

    public double Y
    {
        get;
        set => Set(ref field, value);
    } = y;

    // ---- fiber state & colors ----

    public FiberState State
    {
        get;
        set
        {
            if (Set(ref field, value)) StateBrush = StateColors.For(value);
        }
    } = FiberState.Disposed;

    public IBrush StateBrush
    {
        get;
        private set => Set(ref field, value);
    } = StateColors.For(FiberState.Disposed);

    /// <summary>True when this node is part of a detected dependency cycle (dead island).</summary>
    public bool IsWarning
    {
        get;
        set => Set(ref field, value);
    }

    public bool IsLinkSource
    {
        get;
        set => Set(ref field, value);
    }

    // ---- host-side state (managed by GraphViewModel / FiberHost) ----

    /// <summary>When set, the plugin body throws on load (used to demonstrate Failed).</summary>
    public bool FailRequested { get; set; }

    /// <summary>The real CordiSharp fiber handle (null = not loaded / Disposed).</summary>
    public PluginHandle? Handle { get; set; }

    /// <summary>Service names this node depends on (snapshot taken at load time).</summary>
    public IReadOnlyList<string> ProviderNames { get; set; } = [];
}
