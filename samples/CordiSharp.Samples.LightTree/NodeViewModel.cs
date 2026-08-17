using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CordiSharp.Registry;

namespace CordiSharp.Samples.LightTree;

/// <summary>One light in the tree: a visual node backed by a real CordiSharp plugin fiber.
/// Pure VM: state/position are observable properties (source-generated partial
/// properties); all lifecycle actions are commands that delegate to the graph.</summary>
public sealed partial class NodeViewModel : ObservableObject
{
    public const double Radius = 24;

    private readonly GraphViewModel? _graph;

    public int Id { get; }
    public string Name { get; }

    public NodeViewModel(GraphViewModel? graph, int id, double x, double y)
    {
        _graph = graph;
        Id = id;
        Name = $"N{id}";
        X = x;
        Y = y;
    }

    // ---- position (world coordinates, node center) ----

    [ObservableProperty]
    public partial double X { get; set; }

    [ObservableProperty]
    public partial double Y { get; set; }

    // ---- fiber state & colors ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateBrush))]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial FiberState State { get; set; } = FiberState.Disposed;

    public IBrush StateBrush => StateColors.For(State);
    public string StateText => StateColors.Text(State);

    /// <summary>True when this node is part of a detected dependency cycle (dead island).</summary>
    [ObservableProperty]
    public partial bool IsWarning { get; set; }

    /// <summary>True while this node is the pending "dependent" end of a Ctrl+click connection.</summary>
    [ObservableProperty]
    public partial bool IsLinkSource { get; set; }

    // ---- host-side state (managed by GraphViewModel / FiberHost) ----

    /// <summary>When set, the plugin body throws on load (used to demonstrate Failed).</summary>
    public bool FailRequested { get; set; }

    /// <summary>The real CordiSharp fiber handle (null = not loaded / Disposed).</summary>
    public PluginHandle? Handle { get; set; }

    /// <summary>Service names this node depends on (snapshot taken at load time).</summary>
    public IReadOnlyList<string> ProviderNames { get; set; } = [];

    // ---- commands (bound from the node context menu / diagnostics) ----

    [RelayCommand]
    private Task Start() => _graph!.StartAsync(this);

    [RelayCommand]
    private Task Stop() => _graph!.StopAsync(this);

    [RelayCommand]
    private Task Fail() => _graph!.FailAsync(this);

    [RelayCommand]
    private Task Recover() => _graph!.RecoverAsync(this);

    [RelayCommand]
    private Task Remove() => _graph!.RemoveAsync(this);
}
