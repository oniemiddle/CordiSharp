using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace CordiSharp.Samples.LightTree;

public partial class MainWindow : Window
{
    private readonly GraphViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        Canvas.Graph = _vm;

        AddNodeButton.Click += (_, _) =>
        {
            var pos = Canvas.Bounds.Width > 1
                ? Canvas.ViewportToWorld(new Point(Canvas.Bounds.Width / 2, Canvas.Bounds.Height / 2))
                : new Point(200, 200);
            var node = _vm.AddNode(pos.X, pos.Y);
            ShowInfo($"已添加节点 {node.Name}（灰色 = 未加载）。右键节点选择状态，拖拽可移动。", Brushes.Black);
        };

        DemoButton.Click += async (_, _) => await _vm.LoadDemoAsync();
        StartAllButton.Click += async (_, _) => await _vm.StartAllAsync();
        StopAllButton.Click += async (_, _) => await _vm.StopAllAsync();
        ClearButton.Click += async (_, _) => await _vm.ClearAsync();

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(GraphViewModel.Message) or nameof(GraphViewModel.MessageBrush))
            {
                ShowInfo(_vm.Message, _vm.MessageBrush);
            }
        };

        Loaded += async (_, _) =>
        {
            await _vm.LoadDemoAsync();
            Canvas.CenterView();
            if (Environment.GetCommandLineArgs().Contains("--autodemo"))
            {
                await AutoDemoAsync();
            }
            else if (Environment.GetCommandLineArgs().Contains("--zoomtest"))
            {
                await ZoomTestAsync();
            }
            else if (Environment.GetCommandLineArgs().Contains("--bugrepro"))
            {
                await BugReproAsync();
            }
        };

        Closing += (_, _) => _vm.Dispose();
    }

    /// <summary>Reproduces the reported bug: after demo, add edge N2→N3, stop N3
    /// (N2,N4 go Pending), then start N3 — do N2,N4 come back Active automatically?
    /// Log: %TEMP%/lighttree-bugrepro.log</summary>
    private async Task BugReproAsync()
    {
        var log = new StreamWriter(Path.Combine(Path.GetTempPath(), "lighttree-bugrepro.log"), append: false);
        void Dump(string step)
        {
            log.WriteLine(step + " : " + string.Join(" | ", _vm.Nodes.Select(n => $"{n.Name}={n.State}")));
            log.Flush();
        }

        await _vm.ClearAsync();
        await _vm.LoadDemoAsync();
        Dump("after demo");

        var n2 = _vm.Nodes.First(n => n.Name == "N2");
        var n3 = _vm.Nodes.First(n => n.Name == "N3");
        await _vm.AddEdgeAsync(n2, n3); // N2 depends on N3
        Dump("after edge N2->N3");
        await Task.Delay(300);
        Dump("after edge N2->N3 +300ms");

        await _vm.StopAsync(n3);
        Dump("after stop N3");
        await Task.Delay(300);
        Dump("after stop N3 +300ms");

        await _vm.StartAsync(n3);
        Dump("after start N3");
        await Task.Delay(300);
        Dump("after start N3 +300ms");

        log.Close();
    }

    /// <summary>Programmatic zoom/pan sequence with per-frame alignment verification
    /// (node actual render position vs matrix-expected), to detect node/edge drift.
    /// Log: %TEMP%/lighttree-zoomtest.log</summary>
    private async Task ZoomTestAsync()
    {
        var log = new StreamWriter(Path.Combine(Path.GetTempPath(), "lighttree-zoomtest.log"), append: false);
        void Dump(string step)
        {
            log.WriteLine("=== " + step + " ===");
            log.Write(Canvas.VerifyAlignment());
            log.Flush();
        }

        var center = () => new Point(Canvas.Bounds.Width / 2, Canvas.Bounds.Height / 2);
        await Task.Delay(400);
        Dump("initial");
        Canvas.ZoomBy(2.2, center());
        await Task.Delay(400);
        Dump("zoomin 2.2");
        Canvas.ZoomBy(1.0 / 2.2, center());
        await Task.Delay(400);
        Dump("back");
        Canvas.PanBy(new Vector(180, 60));
        await Task.Delay(400);
        Dump("pan");
        Canvas.ZoomBy(1.6, new Point(200, 150));
        await Task.Delay(400);
        Dump("zoom-corner");
        log.Close();
    }

    /// <summary>Replays the user-visible cascade scenario and dumps node states to a
    /// log file, so a broken UI-level cascade is visible without manual interaction.
    /// Log: %TEMP%/cordisharp-lighttree-autodemo.log</summary>
    private async Task AutoDemoAsync()
    {
        var log = new StreamWriter(
            Path.Combine(Path.GetTempPath(), "cordisharp-lighttree-autodemo.log"),
            append: false);
        void Dump(string step)
        {
            log.WriteLine(step + " : " + string.Join(" | ", _vm.Nodes.Select(n => $"{n.Name}={n.State}")));
            log.Flush();
        }

        Dump("after demo");
        await Task.Delay(300);
        Dump("after demo +300ms");

        var n1 = _vm.Nodes.First();
        await _vm.StopAsync(n1); // expect N2,N3,N4 -> Pending
        Dump("after stop N1");
        await Task.Delay(500);
        Dump("after stop N1 +500ms");

        await _vm.StartAsync(n1); // expect cascade back to Active
        Dump("after start N1");
        await Task.Delay(500);
        Dump("after start N1 +500ms");

        var n2 = _vm.Nodes.First(n => n.Id == 2);
        await _vm.FailAsync(n2); // N2 red; N4 (depends on N2) -> Pending
        Dump("after fail N2");
        await Task.Delay(500);
        Dump("after fail N2 +500ms");

        await _vm.RecoverAsync(n2);
        Dump("after recover N2");
        await Task.Delay(500);
        Dump("after recover N2 +500ms");

        // cycle: stop N3/N4, add N3<->N4 edges, start them → dead island (both Pending)
        var n3 = _vm.Nodes.First(n => n.Id == 3);
        var n4 = _vm.Nodes.First(n => n.Id == 4);
        await _vm.StopAsync(n3);
        await _vm.AddEdgeAsync(n3, n4); // N3 depends on N4
        await _vm.AddEdgeAsync(n4, n3); // N4 depends on N3 → cycle
        Dump("after cycle edges");
        await _vm.StartAsync(n3);
        await _vm.StartAsync(n4);
        Dump("after starting cycle (expect N3,N4 Pending + warning)");
        await Task.Delay(500);
        Dump("after starting cycle +500ms");

        log.Close();
    }

    private void ShowInfo(string message, IBrush brush)
    {
        InfoText.Text = message;
        InfoText.Foreground = brush;
        InfoBar.IsVisible = message.Length > 0;
    }
}
