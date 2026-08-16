using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;

namespace CordiSharp.Samples.LightTree;

/// <summary>
/// A canvas-style graph editor: free panning (drag on background), zoom (mouse wheel,
/// anchored at the cursor), grid background, directed dependency edges with arrowheads,
/// and draggable node "lights".
///
/// Layout: this control is a <see cref="Panel"/> with two children —
/// <see cref="_edgeLayer"/> (grid + edges; fills the viewport and applies the pan/zoom
/// matrix itself, so it always covers the whole background) and <see cref="_world"/>
/// (a world <see cref="Canvas"/> holding the node views, transformed by the same matrix).
/// </summary>
public sealed class GraphCanvas : Panel
{
    public static readonly StyledProperty<GraphViewModel?> GraphProperty =
        AvaloniaProperty.Register<GraphCanvas, GraphViewModel?>(nameof(Graph));

    public GraphViewModel? Graph
    {
        get => GetValue(GraphProperty);
        set => SetValue(GraphProperty, value);
    }

    private readonly Canvas _world = new();
    private readonly MatrixTransform _worldTransform = new(Matrix.Identity);
    private readonly EdgeLayer _edgeLayer = new()
    {
        IsHitTestVisible = false,
        ClipToBounds = false,
    };
    private readonly Dictionary<NodeViewModel, FiberNodeView> _views = new();
    private readonly Dictionary<NodeViewModel, PropertyChangedEventHandler> _nodeHandlers = new();

    private Matrix _viewMatrix = Matrix.Identity;
    private double _zoom = 1;
    private bool _panning;
    private Point _lastViewport;

    public GraphCanvas()
    {
        ClipToBounds = true;
        Background = new SolidColorBrush(Color.Parse("#FAFAFA"));
        _world.RenderTransform = _worldTransform;
        // RenderTransform applies T(origin)·M·T(-origin) around RenderTransformOrigin.
        // Default origin is NOT (0,0), which would make the world canvas transform
        // differ from the EdgeLayer's raw matrix → node/edge drift on zoom. Pin (0,0).
        _world.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
        // edge layer first (stretched to the viewport, drawn under), world canvas on top
        Children.Add(_edgeLayer);
        Children.Add(_world);

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnPointerWheelChanged;
        SizeChanged += (_, _) => InvalidateEdges();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GraphProperty)
        {
            DetachGraph();
            AttachGraph(change.GetNewValue<GraphViewModel?>());
        }
    }

    // ---- attach / detach ----

    private void AttachGraph(GraphViewModel? graph)
    {
        if (graph is null) return;
        graph.Nodes.CollectionChanged += OnNodesChanged;
        graph.Edges.CollectionChanged += OnEdgesChanged;
        graph.GraphChanged += OnGraphChanged;
        foreach (var node in graph.Nodes) AddView(node);
        InvalidateEdges();
    }

    private void DetachGraph()
    {
        if (Graph is null) return;
        Graph.Nodes.CollectionChanged -= OnNodesChanged;
        Graph.Edges.CollectionChanged -= OnEdgesChanged;
        Graph.GraphChanged -= OnGraphChanged;
        foreach (var node in Graph.Nodes.ToList()) RemoveView(node);
        _world.Children.Clear();
    }

    private void OnGraphChanged() => InvalidateEdges();

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (NodeViewModel node in e.OldItems) RemoveView(node);
        }
        if (e.NewItems is not null)
        {
            foreach (NodeViewModel node in e.NewItems) AddView(node);
        }
        InvalidateEdges();
    }

    private void OnEdgesChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateEdges();

    // ---- node views ----

    private void AddView(NodeViewModel node)
    {
        var view = new FiberNodeView(node)
        {
            WorldCanvas = _world,
        };
        view.CtrlClicked += OnNodeCtrlClicked;
        view.ContextMenu = BuildNodeMenu(node);
        _views[node] = view;
        _world.Children.Add(view);

        void Handler(object? s, PropertyChangedEventArgs args) => OnNodePropertyChanged(node, args.PropertyName);
        _nodeHandlers[node] = Handler;
        node.PropertyChanged += Handler;

        PositionView(node);
        view.Refresh();
    }

    private void RemoveView(NodeViewModel node)
    {
        if (_nodeHandlers.TryGetValue(node, out var handler))
        {
            node.PropertyChanged -= handler;
            _nodeHandlers.Remove(node);
        }
        if (_views.Remove(node, out var view))
        {
            _world.Children.Remove(view);
        }
    }

    private void OnNodePropertyChanged(NodeViewModel node, string? propertyName)
    {
        if (propertyName is nameof(NodeViewModel.X) or nameof(NodeViewModel.Y))
        {
            PositionView(node);
            InvalidateEdges(); // edges follow the node
        }
        else if (propertyName is nameof(NodeViewModel.State) or nameof(NodeViewModel.StateBrush)
                 or nameof(NodeViewModel.IsWarning) or nameof(NodeViewModel.IsLinkSource))
        {
            if (_views.TryGetValue(node, out var view)) view.Refresh();
        }
    }

    private void PositionView(NodeViewModel node)
    {
        if (!_views.TryGetValue(node, out var view)) return;
        Canvas.SetLeft(view, node.X - FiberNodeView.Radius - 8);
        Canvas.SetTop(view, node.Y - FiberNodeView.Radius - 8);
    }

    private void OnNodeCtrlClicked(NodeViewModel node)
    {
        if (Graph is null) return;
        if (Graph.LinkSource is null)
        {
            Graph.LinkSource = node;
            ShowHint($"已选中 {node.Name} 作为依赖方：Ctrl+点击提供方，无则建边、有则删边（自动取反）。");
        }
        else if (ReferenceEquals(Graph.LinkSource, node))
        {
            Graph.LinkSource = null;
            ShowHint("已取消选中。");
        }
        else
        {
            var dep = Graph.LinkSource;
            Graph.LinkSource = null;
            _ = Graph.ToggleEdgeAsync(dep, node);
        }
    }

    private void ShowHint(string text)
    {
        Graph?.ShowHint(text);
    }

    // ---- node context menu ----

    private ContextMenu BuildNodeMenu(NodeViewModel node)
    {
        var menu = new ContextMenu();

        var start = new MenuItem { Header = "启动 (→ Active)" };
        start.Click += (_, _) => _ = Graph?.StartAsync(node);

        var stop = new MenuItem { Header = "停用 (→ Disposed)" };
        stop.Click += (_, _) => _ = Graph?.StopAsync(node);

        var fail = new MenuItem { Header = "注入故障 (→ Failed)" };
        fail.Click += (_, _) => _ = Graph?.FailAsync(node);

        var recover = new MenuItem { Header = "恢复 (Failed → Active)" };
        recover.Click += (_, _) => _ = Graph?.RecoverAsync(node);

        var remove = new MenuItem { Header = "删除节点" };
        remove.Click += (_, _) => _ = Graph?.RemoveAsync(node);

        menu.Items.Add(start);
        menu.Items.Add(stop);
        menu.Items.Add(fail);
        menu.Items.Add(recover);
        menu.Items.Add(new Separator());
        menu.Items.Add(remove);
        return menu;
    }

    // ---- background interactions: pan / zoom / edge deletion / link cancel ----

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);

        if (point.Properties.IsRightButtonPressed)
        {
            // edge deletion: only when the press is not on a node (nodes open their own menu)
            var viewport = e.GetPosition(this);
            var world = viewport.Transform(_viewMatrix.Invert());
            if (!IsOverNode(world) && HitTestEdge(world) is { } edge)
            {
                ShowEdgeMenu(edge);
                e.Handled = true;
                return;
            }
        }

        if (!point.Properties.IsLeftButtonPressed) return;
        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            // Ctrl+click on empty canvas: cancel the pending connection
            Graph?.CancelLink();
            e.Handled = true;
            return;
        }

        _panning = true;
        _lastViewport = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_panning) return;
        var current = e.GetPosition(this);
        var delta = current - _lastViewport;
        _viewMatrix = _viewMatrix * Matrix.CreateTranslation(delta.X, delta.Y);
        _lastViewport = current;
        ApplyView();
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (Graph is null) return;
        var position = e.GetPosition(this);
        var factor = e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1;
        var newZoom = Math.Clamp(_zoom * factor, 0.2, 4.0);
        if (Math.Abs(newZoom - _zoom) < 1e-9) return;
        factor = newZoom / _zoom;
        _zoom = newZoom;
        // zoom anchored at the cursor: M' = M * T(-pos) * S(f) * T(pos)
        _viewMatrix = _viewMatrix
            * Matrix.CreateTranslation(-position.X, -position.Y)
            * Matrix.CreateScale(factor, factor)
            * Matrix.CreateTranslation(position.X, position.Y);
        ApplyView();
        e.Handled = true;
    }

    private void ApplyView()
    {
        _worldTransform.Matrix = _viewMatrix;
        InvalidateEdges();
    }

    private void InvalidateEdges() => _edgeLayer.Update(Graph, _viewMatrix, Bounds.Size);

    /// <summary>Programmatic zoom anchored at a viewport point (mirrors the wheel handler).</summary>
    public void ZoomBy(double factor, Point at)
    {
        var newZoom = Math.Clamp(_zoom * factor, 0.2, 4.0);
        if (Math.Abs(newZoom - _zoom) < 1e-9) return;
        factor = newZoom / _zoom;
        _zoom = newZoom;
        _viewMatrix = _viewMatrix
            * Matrix.CreateTranslation(-at.X, -at.Y)
            * Matrix.CreateScale(factor, factor)
            * Matrix.CreateTranslation(at.X, at.Y);
        ApplyView();
    }

    /// <summary>Programmatic pan by a viewport delta (mirrors the drag handler).</summary>
    public void PanBy(Vector delta)
    {
        _viewMatrix = _viewMatrix * Matrix.CreateTranslation(delta.X, delta.Y);
        ApplyView();
    }

    /// <summary>Compares each node's ACTUAL rendered position (TranslatePoint, includes the
    /// world RenderTransform) against the matrix-expected position, to detect alignment
    /// drift between node controls and the manually drawn edge layer.</summary>
    public string VerifyAlignment()
    {
        var sb = new StringBuilder();
        foreach (var (node, view) in _views)
        {
            var actual = view.TranslatePoint(new Point(FiberNodeView.Radius + 8, FiberNodeView.Radius + 8), this);
            var expected = new Point(node.X, node.Y).Transform(_viewMatrix);
            var dx = (actual?.X ?? double.NaN) - expected.X;
            var dy = (actual?.Y ?? double.NaN) - expected.Y;
            sb.AppendLine($"{node.Name}: actual=({actual?.X ?? double.NaN:F1},{actual?.Y ?? double.NaN:F1}) " +
                          $"expected=({expected.X:F1},{expected.Y:F1}) diff=({dx:F2},{dy:F2})");
        }
        return sb.ToString();
    }

    // ---- edge hit testing (rendering lives in EdgeLayer) ----

    private EdgeViewModel? HitTestEdge(Point world)
    {
        if (Graph is null) return null;
        foreach (var edge in Graph.Edges)
        {
            var from = new Point(edge.From.X, edge.From.Y);
            var to = new Point(edge.To.X, edge.To.Y);
            if (DistanceToSegment(world, from, to) < 9) return edge;
        }
        return null;
    }

    private bool IsOverNode(Point world)
    {
        if (Graph is null) return false;
        foreach (var node in Graph.Nodes)
        {
            var dx = world.X - node.X;
            var dy = world.Y - node.Y;
            if (dx * dx + dy * dy < (FiberNodeView.Radius + 8) * (FiberNodeView.Radius + 8)) return true;
        }
        return false;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var abX = b.X - a.X;
        var abY = b.Y - a.Y;
        var apX = p.X - a.X;
        var apY = p.Y - a.Y;
        var lengthSquared = abX * abX + abY * abY;
        if (lengthSquared < 1e-9)
        {
            var dx = p.X - a.X;
            var dy = p.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
        var t = Math.Clamp((apX * abX + apY * abY) / lengthSquared, 0, 1);
        var projX = a.X + abX * t;
        var projY = a.Y + abY * t;
        var rx = p.X - projX;
        var ry = p.Y - projY;
        return Math.Sqrt(rx * rx + ry * ry);
    }

    private void ShowEdgeMenu(EdgeViewModel edge)
    {
        var menu = new ContextMenu { Placement = PlacementMode.Pointer };
        var remove = new MenuItem { Header = $"删除连线 {edge.From.Name}→{edge.To.Name}" };
        remove.Click += (_, _) => _ = Graph?.RemoveEdgeAsync(edge);
        menu.Items.Add(remove);
        menu.Open(this);
    }

    // ---- helpers for the window ----

    /// <summary>Converts a viewport point to world coordinates.</summary>
    public Point ViewportToWorld(Point viewport) => viewport.Transform(_viewMatrix.Invert());

    /// <summary>Centers the current nodes and fits them into the viewport.</summary>
    public void CenterView()
    {
        if (Graph is null || Graph.Nodes.Count == 0) return;
        var minX = Graph.Nodes.Min(n => n.X);
        var maxX = Graph.Nodes.Max(n => n.X);
        var minY = Graph.Nodes.Min(n => n.Y);
        var maxY = Graph.Nodes.Max(n => n.Y);

        var width = Math.Max(maxX - minX + 220, 320);
        var height = Math.Max(maxY - minY + 220, 320);
        _zoom = Math.Clamp(Math.Min(Bounds.Width / width, Bounds.Height / height), 0.2, 2.0);

        var centerX = (minX + maxX) / 2;
        var centerY = (minY + maxY) / 2;
        var panX = Bounds.Width / 2 - centerX * _zoom;
        var panY = Bounds.Height / 2 - centerY * _zoom;
        _viewMatrix = Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(panX, panY);
        ApplyView();
    }
}

/// <summary>
/// Draws the grid and dependency edges. This control is stretched over the whole
/// <see cref="GraphCanvas"/> viewport (so the grid always covers the entire background)
/// and applies the pan/zoom matrix itself: all drawing happens in world coordinates
/// inside <see cref="PushTransform"/>. <see cref="Control.Render"/> is overridable here
/// (unlike <see cref="Panel"/>, which seals it).
/// </summary>
internal sealed class EdgeLayer : Control
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

        // Avalonia 11: Push* returns an IDisposable state (Pop() was removed)
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

    private void DrawEdge(DrawingContext dc, EdgeViewModel edge)
    {
        var from = new Point(edge.From.X, edge.From.Y);
        var to = new Point(edge.To.X, edge.To.Y);
        var direction = new Vector(to.X - from.X, to.Y - from.Y);
        if (direction.Length < 1) return;
        var normal = direction / direction.Length;

        // shorten so the arrow stops at the provider's circle border
        var start = from + normal * (FiberNodeView.Radius + 2);
        var end = to - normal * (FiberNodeView.Radius + 6);

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
