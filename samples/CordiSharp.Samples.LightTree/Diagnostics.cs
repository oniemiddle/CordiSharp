using Avalonia;
using Avalonia.Media.Imaging;

namespace CordiSharp.Samples.LightTree;

/// <summary>
/// Diagnostic driver modes (kept out of the window code-behind):
///   --autodemo  GUI script: stop/start/fail/recover/cycle, states → %TEMP%/cordisharp-lighttree-autodemo.log
///   --bugrepro  reproduces the "cascade does not recover after provider restart" scenario
///   --zoomtest  programmatic zoom/pan with node/edge alignment verification
/// </summary>
internal static class Diagnostics
{
    public static async Task RunIfRequested(GraphViewModel vm, GraphCanvas canvas)
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--autodemo")) await AutoDemoAsync(vm);
        else if (args.Contains("--bugrepro")) await BugReproAsync(vm);
        else if (args.Contains("--zoomtest")) await ZoomTestAsync(vm, canvas);
        else if (args.Contains("--vischeck")) await VisCheckAsync(vm, canvas);
        else if (args.Contains("--dragtest")) await DragTestAsync(vm, canvas);
    }

    /// <summary>Moves N1 programmatically (simulating a drag), then snapshots the canvas:
    /// the edge layer must redraw at the node's new position without a pan/zoom kick.
    /// Snapshot: %TEMP%/lighttree-dragtest.png</summary>
    private static async Task DragTestAsync(GraphViewModel vm, GraphCanvas canvas)
    {
        await Task.Delay(500);
        var log = OpenLog("lighttree-dragtest.log");
        var n1 = vm.Nodes.First(n => n.Name == "N1");
        var oldX = n1.X;
        n1.X += 80; // simulated drag
        await Task.Delay(300);
        var width = (int)canvas.Bounds.Width;
        var height = (int)canvas.Bounds.Height;
        var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(
            new PixelSize(width, height), new Vector(96, 96));
        bmp.Render(canvas);
        var path = Path.Combine(Path.GetTempPath(), "lighttree-dragtest.png");
        await using (var stream = File.Create(path))
        {
            bmp.Save(stream, new PngBitmapEncoderOptions());
            await log.WriteLineAsync($"moved N1 {oldX} -> {n1.X}; snapshot: {path} ({stream.Length} bytes)");
        }
        log.Close();
    }

    /// <summary>Renders the canvas to a PNG snapshot so node visibility can be checked
    /// directly. Snapshot: %TEMP%/lighttree-snapshot.png</summary>
    private static async Task VisCheckAsync(GraphViewModel vm, GraphCanvas canvas)
    {
        await Task.Delay(600);
        var width = (int)canvas.Bounds.Width;
        var height = (int)canvas.Bounds.Height;
        var log = OpenLog("lighttree-vischeck.log");
        var bmp = new RenderTargetBitmap(
            new PixelSize(width, height), new Vector(96, 96));
        bmp.Render(canvas);
        var path = Path.Combine(Path.GetTempPath(), "lighttree-snapshot.png");
        await using (var stream = File.Create(path))
        {
            bmp.Save(stream, new PngBitmapEncoderOptions());
            await log.WriteLineAsync($"saved snapshot: {path} ({stream.Length} bytes) viewport {width}x{height}");
        }
        log.Close();
    }

    private static StreamWriter OpenLog(string name) =>
        new(Path.Combine(Path.GetTempPath(), name), append: false);

    private static void Dump(StreamWriter log, GraphViewModel vm, string step)
    {
        log.WriteLine(step + " : " + string.Join(" | ", vm.Nodes.Select(n => $"{n.Name}={n.State}")));
        log.Flush();
    }

    private static async Task AutoDemoAsync(GraphViewModel vm)
    {
        var log = OpenLog("cordisharp-lighttree-autodemo.log");
        Dump(log, vm, "after demo");
        await Task.Delay(300);
        Dump(log, vm, "after demo +300ms");

        var n1 = vm.Nodes.First(n => n.Name == "N1");
        await vm.StopAsync(n1);
        Dump(log, vm, "after stop N1");
        await Task.Delay(500);
        Dump(log, vm, "after stop N1 +500ms");

        await vm.StartAsync(n1);
        Dump(log, vm, "after start N1");
        await Task.Delay(500);
        Dump(log, vm, "after start N1 +500ms");

        var n2 = vm.Nodes.First(n => n.Name == "N2");
        await vm.FailAsync(n2);
        Dump(log, vm, "after fail N2");
        await Task.Delay(500);
        Dump(log, vm, "after fail N2 +500ms");

        await vm.RecoverAsync(n2);
        Dump(log, vm, "after recover N2");
        await Task.Delay(500);
        Dump(log, vm, "after recover N2 +500ms");

        // cycle: stop N3/N4, add N3<->N4 edges, start them → dead island (both Pending)
        var n3 = vm.Nodes.First(n => n.Name == "N3");
        var n4 = vm.Nodes.First(n => n.Name == "N4");
        await vm.StopAsync(n3);
        await vm.StopAsync(n4);
        await vm.AddEdgeAsync(n3, n4); // N3 depends on N4
        await vm.AddEdgeAsync(n4, n3); // N4 depends on N3 → cycle
        Dump(log, vm, "after cycle edges");
        await vm.StartAsync(n3);
        await vm.StartAsync(n4);
        Dump(log, vm, "after starting cycle (expect N3,N4 Pending + warning)");
        await Task.Delay(500);
        Dump(log, vm, "after starting cycle +500ms");

        log.Close();
    }

    private static async Task BugReproAsync(GraphViewModel vm)
    {
        await vm.ClearAsync();
        await vm.LoadDemoAsync();
        var log = OpenLog("lighttree-bugrepro.log");
        Dump(log, vm, "after demo");

        var n2 = vm.Nodes.First(n => n.Name == "N2");
        var n3 = vm.Nodes.First(n => n.Name == "N3");
        await vm.AddEdgeAsync(n2, n3); // N2 depends on N3
        Dump(log, vm, "after edge N2->N3");
        await Task.Delay(300);
        Dump(log, vm, "after edge N2->N3 +300ms");

        await vm.StopAsync(n3);
        Dump(log, vm, "after stop N3");
        await Task.Delay(300);
        Dump(log, vm, "after stop N3 +300ms");

        await vm.StartAsync(n3);
        Dump(log, vm, "after start N3");
        await Task.Delay(300);
        Dump(log, vm, "after start N3 +300ms");

        log.Close();
    }

    private static async Task ZoomTestAsync(GraphViewModel vm, GraphCanvas canvas)
    {
        var log = OpenLog("lighttree-zoomtest.log");

        await Task.Delay(400);
        DumpInternal("initial");
        canvas.ZoomBy(2.2, Center());
        await Task.Delay(400);
        DumpInternal("zoomin 2.2");
        canvas.ZoomBy(1.0 / 2.2, Center());
        await Task.Delay(400);
        DumpInternal("back");
        canvas.PanBy(new Vector(180, 60));
        await Task.Delay(400);
        DumpInternal("pan");
        canvas.ZoomBy(1.6, new Point(200, 150));
        await Task.Delay(400);
        DumpInternal("zoom-corner");
        log.Close();
        return;

        void DumpInternal(string step)
        {
            log.WriteLine("=== " + step + " ===");
            log.Write(canvas.VerifyAlignment());
            log.Flush();
        }

        Point Center() => new(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2);
    }
}
