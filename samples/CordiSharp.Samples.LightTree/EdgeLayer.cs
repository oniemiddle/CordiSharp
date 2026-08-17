using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CordiSharp.Samples.LightTree;

/// <summary>
/// Draws the grid and dependency edges. Stretched over the whole <see cref="GraphCanvas"/>
/// viewport (defined in the control theme), applies the pan/zoom matrix itself: all drawing
/// happens in world coordinates inside <see cref="PushTransform"/>. Public so the theme can
/// reference it in XAML.
/// </summary>
public sealed class EdgeLayer : Control
{
    private GraphViewModel? _graph;
    private Matrix _viewMatrix = Matrix.Identity;
    private Size _viewport;

    public void Update(GraphViewModel? graph, Matrix viewMatrix, Size viewport)
    {
        _graph = graph;
        _viewMatrix = viewMatrix;
        _viewport = viewport;
        InvalidateVisual();
    }

    public override void Render(DrawingContext dc)
    {
        base.Render(dc);
        if (_graph is null) return;

        // Avalonia 11+: Push* returns an IDisposable state (Pop() was removed)
        using (dc.PushTransform(_viewMatrix))
        {
            DrawGrid(dc);
            foreach (var edge in _graph.Edges) DrawEdge(dc, edge);
        }
    }

    private void DrawGrid(DrawingContext dc)
    {
        var inverse = _viewMatrix.Invert();
        var topLeft = new Point(0, 0).Transform(inverse);
        var bottomRight = new Point(_viewport.Width, _viewport.Height).Transform(inverse);
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), 1);
        const double step = 60;

        for (var x = Math.Floor(topLeft.X / step) * step; x <= bottomRight.X; x += step)
        {
            dc.DrawLine(pen, new Point(x, topLeft.Y), new Point(x, bottomRight.Y));
        }
        for (var y = Math.Floor(topLeft.Y / step) * step; y <= bottomRight.Y; y += step)
        {
            dc.DrawLine(pen, new Point(topLeft.X, y), new Point(bottomRight.X, y));
        }
    }

    private static void DrawEdge(DrawingContext dc, EdgeViewModel edge)
    {
        var from = new Point(edge.From.X, edge.From.Y);
        var to = new Point(edge.To.X, edge.To.Y);
        var direction = new Vector(to.X - from.X, to.Y - from.Y);
        if (direction.Length < 1) return;
        var normal = direction / direction.Length;

        // shorten so the arrow stops at the provider's circle border
        var start = from + normal * (NodeViewModel.Radius + 2);
        var end = to - normal * (NodeViewModel.Radius + 6);

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(170, 55, 71, 79)), 2);
        dc.DrawLine(pen, start, end);

        // arrowhead
        var angle = Math.Atan2(normal.Y, normal.X);
        var left = new Point(
            end.X - 11 * Math.Cos(angle - 0.45),
            end.Y - 11 * Math.Sin(angle - 0.45));
        var right = new Point(
            end.X - 11 * Math.Cos(angle + 0.45),
            end.Y - 11 * Math.Sin(angle + 0.45));

        var arrow = new StreamGeometry();
        using (var ctx = arrow.Open())
        {
            ctx.BeginFigure(end, true);
            ctx.LineTo(left);
            ctx.LineTo(right);
            ctx.EndFigure(true);
        }
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(190, 55, 71, 79)), null, arrow);
    }
}