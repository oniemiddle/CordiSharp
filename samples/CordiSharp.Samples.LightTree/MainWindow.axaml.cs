using Avalonia;
using Avalonia.Controls;

namespace CordiSharp.Samples.LightTree;

/// <summary>
/// Thin view shell: wires the DataContext and injects the one piece of view-only
/// geometry the VM needs (where a new node appears). All other logic lives in
/// <see cref="GraphViewModel"/> / <see cref="NodeViewModel"/>; the XAML binds
/// toolbar commands, the info bar and the node template directly.
/// </summary>
public partial class MainWindow : Window
{
    private readonly GraphViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        // view-injected service: world point where a new node appears (viewport center)
        _vm.AddNodePointProvider = () => Canvas.Bounds.Width > 1
            ? Canvas.ViewportToWorld(new Point(Canvas.Bounds.Width / 2, Canvas.Bounds.Height / 2))
            : new Point(200, 200);

        Loaded += async (_, _) =>
        {
            await _vm.LoadDemoAsync();
            Canvas.CenterView();
            await Diagnostics.RunIfRequested(_vm, Canvas);
        };

        Closing += (_, _) => _vm.Dispose();
    }
}
