using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.Media;

namespace CordiSharp.Samples.LightTree;

/// <summary>
/// A canvas-style graph editor. Pure logic — the entire visual structure lives in the
/// control theme (App.axaml): an <see cref="EdgeLayer"/> stretched over the viewport,
/// and a world <see cref="Canvas"/> (PART_World, pan/zoom RenderTransform) hosting an
/// <see cref="ItemsControl"/> (PART_Items) that renders nodes via the shared
/// <see cref="DataTemplate"/> and positions them on a Canvas items panel via
/// ItemContainerTheme Canvas.Left/Top bindings.
///
/// Pointer interaction (pan / zoom / node drag / Ctrl+click connect / edge deletion) is
/// handled centrally here via hit testing — the node template carries no interaction code.
/// </summary>
[TemplatePart(PART_World, typeof(Canvas), IsRequired = true)]
[TemplatePart(PART_Edges, typeof(EdgeLayer), IsRequired = true)]
[TemplatePart(PART_Items, typeof(ItemsControl), IsRequired = true)]
public sealed class GraphCanvas : TemplatedControl
{
    public const string PART_World = "PART_World";
    public const string PART_Edges = "PART_Edges";
    public const string PART_Items = "PART_Items";
    
    public static readonly StyledProperty<GraphViewModel?> GraphProperty =
        AvaloniaProperty.Register<GraphCanvas, GraphViewModel?>(nameof(Graph));

    public GraphViewModel? Graph
    {
        get => GetValue(GraphProperty);
        set => SetValue(GraphProperty, value);
    }

    // template parts (wired in OnApplyTemplate)
    private Canvas? _world;
    private MatrixTransform? _worldTransform;
    private EdgeLayer? _edgeLayer;
    private ItemsControl? _nodeItems;

    private GraphViewModel? _graph;
    private Matrix _viewMatrix = Matrix.Identity;
    private double _zoom = 1;

    // ---- interaction state ----
    private bool _panning;
    private Point _lastViewport;
    private NodeViewModel? _dragNode;
    private Point _pressWorld;
    private double _dragStartX;
    private double _dragStartY;
    private NodeViewModel? _ctrlNode;
    private Point _ctrlPressWorld;

    public GraphCanvas()
    {
        ClipToBounds = true;

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnPointerWheelChanged;
        SizeChanged += (_, _) => InvalidateEdges();
    }
    
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _world = e.NameScope.Find<Canvas>(PART_World);
        _edgeLayer = e.NameScope.Find<EdgeLayer>(PART_Edges);
        _nodeItems = e.NameScope.Find<ItemsControl>(PART_Items);
        // the world's pan/zoom transform is a MatrixTransform wired here (transform
        // objects are not named StyledElements, so they cannot be declared with x:Name)
        _worldTransform = new MatrixTransform(_viewMatrix);
        _world!.RenderTransform = _worldTransform;
        InvalidateEdges();
        TryAttach();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GraphProperty)
        {
            DetachGraph();
            _graph = change.GetNewValue<GraphViewModel?>();
            TryAttach();
        }
    }

    /// <summary>Attaches the graph once both the VM and the template parts exist.</summary>
    private void TryAttach()
    {
        if (_graph is not null && _nodeItems is not null) AttachGraph(_graph);
    }

    private void AttachGraph(GraphViewModel graph)
    {
        graph.Nodes.CollectionChanged += OnNodesChanged;
        graph.Edges.CollectionChanged += OnEdgesChanged;
        graph.GraphChanged += OnGraphChanged;
        foreach (var node in graph.Nodes) SubscribeNode(node);
        InvalidateEdges();
    }

    private void DetachGraph()
    {
        if (_graph is null) return;
        _graph.Nodes.CollectionChanged -= OnNodesChanged;
        _graph.Edges.CollectionChanged -= OnEdgesChanged;
        _graph.GraphChanged -= OnGraphChanged;
        foreach (var node in _graph.Nodes.ToList()) UnsubscribeNode(node);
    }

    private void OnGraphChanged() => InvalidateEdges();
    private void OnEdgesChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateEdges();

    // ---- node position tracking (edges must follow the node) ----

    private readonly HashSet<NodeViewModel> _subscribedNodes = [];

    private void SubscribeNode(NodeViewModel node)
    {
        if (!_subscribedNodes.Add(node)) return;
        node.PropertyChanged += OnNodePropertyChanged;
    }

    private void UnsubscribeNode(NodeViewModel node)
    {
        if (!_subscribedNodes.Remove(node)) return;
        node.PropertyChanged -= OnNodePropertyChanged;
    }

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var node in _subscribedNodes.ToList()) UnsubscribeNode(node);
        }
        else
        {
            if (e.OldItems is not null) foreach (NodeViewModel node in e.OldItems) UnsubscribeNode(node);
            if (e.NewItems is not null) foreach (NodeViewModel node in e.NewItems) SubscribeNode(node);
        }
        InvalidateEdges();
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NodeViewModel.X) or nameof(NodeViewModel.Y))
        {
            InvalidateEdges(); // edges follow the node while dragging
        }
    }

    // ---- connection selection (Ctrl+left click) ----

    private void OnNodeCtrlClicked(NodeViewModel node)
    {
        if (_graph is null) return;
        if (_graph.LinkSource is null)
        {
            _graph.LinkSource = node;
            ShowHint($"已选中 {node.Name} 作为依赖方：Ctrl+点击提供方，无则建边、有则删边（自动取反）。");
        }
        else if (ReferenceEquals(_graph.LinkSource, node))
        {
            _graph.LinkSource = null;
            ShowHint("已取消选中。");
        }
        else
        {
            var dep = _graph.LinkSource;
            _graph.LinkSource = null;
            _ = _graph.ToggleEdgeAsync(dep, node);
        }
    }

    private void ShowHint(string text) => _graph?.ShowHint(text);

    // ---- pointer interaction: node drag / connect / pan / edge deletion ----

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var viewport = e.GetPosition(this);
        var world = ViewportToWorld(viewport);

        if (point.Properties.IsRightButtonPressed)
        {
            // edge deletion: only when the press is not on a node (nodes open their own menu)
            if (HitTestNode(world) is null && HitTestEdge(world) is { } edge)
            {
                ShowEdgeMenu(edge);
                e.Handled = true;
            }
            return;
        }

        if (!point.Properties.IsLeftButtonPressed) return;

        var node = HitTestNode(world);
        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            // Ctrl+click: connect/cancel-connect selection (nodes) or cancel (empty canvas)
            if (node is not null)
            {
                _ctrlNode = node;
                _ctrlPressWorld = world;
            }
            else
            {
                _graph?.CancelLink();
            }
            e.Handled = true;
            return;
        }

        if (node is not null)
        {
            // plain left button on a node: drag it (never changes selection state)
            _dragNode = node;
            _pressWorld = world;
            _dragStartX = node.X;
            _dragStartY = node.Y;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // empty canvas: pan
        _panning = true;
        _lastViewport = viewport;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragNode is not null)
        {
            var current = ViewportToWorld(e.GetPosition(this));
            var dx = current.X - _pressWorld.X;
            var dy = current.Y - _pressWorld.Y;
            _dragNode.X = Math.Max(0, _dragStartX + dx);
            _dragNode.Y = Math.Max(0, _dragStartY + dy);
            e.Handled = true;
            return;
        }

        if (!_panning) return;
        var viewport = e.GetPosition(this);
        var delta = viewport - _lastViewport;
        _viewMatrix *= Matrix.CreateTranslation(delta.X, delta.Y);
        _lastViewport = viewport;
        ApplyView();
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_ctrlNode is not null)
        {
            var released = ViewportToWorld(e.GetPosition(this));
            var dx = released.X - _ctrlPressWorld.X;
            var dy = released.Y - _ctrlPressWorld.Y;
            if (Math.Sqrt(dx * dx + dy * dy) < 4) OnNodeCtrlClicked(_ctrlNode);
            _ctrlNode = null;
            e.Handled = true;
            return;
        }

        if (_dragNode is not null)
        {
            _dragNode = null;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (!_panning) return;
        _panning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_graph is null) return;
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
        _worldTransform?.Matrix = _viewMatrix;
        InvalidateEdges();
    }

    private void InvalidateEdges() => _edgeLayer?.Update(_graph, _viewMatrix, Bounds.Size);

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
        _viewMatrix *= Matrix.CreateTranslation(delta.X, delta.Y);
        ApplyView();
    }

    /// <summary>Compares each node's ACTUAL rendered position (via its ItemsControl
    /// container, includes the world RenderTransform) against the matrix-expected
    /// position, to detect alignment drift between nodes and the drawn edge layer.</summary>
    public string VerifyAlignment()
    {
        var sb = new StringBuilder();
        if (_graph is null || _nodeItems is null) return sb.ToString();
        sb.AppendLine($"items: desired={_nodeItems.DesiredSize} bounds={_nodeItems.Bounds.Size} " +
                      $"panelRoot={( _nodeItems.ItemsPanelRoot is null ? "null" : _nodeItems.ItemsPanelRoot.Bounds.Size.ToString())} " +
                      $"template={(_nodeItems.ItemTemplate is null ? "null" : "set")}");
        foreach (var node in _graph.Nodes)
        {
            var container = _nodeItems.ContainerFromItem(node) as ContentPresenter;
            var actual = container?.TranslatePoint(new Point(0, 0), this);
            var expected = new Point(node.X, node.Y).Transform(_viewMatrix);
            var dx = (actual?.X ?? double.NaN) - expected.X;
            var dy = (actual?.Y ?? double.NaN) - expected.Y;
            var child = container?.Child is { } c ? c.GetType().Name : "null";
            var childBounds = container?.Child is { } cc ? cc.Bounds.Size.ToString() : "?";
            var containerBounds = container?.Bounds.Size.ToString() ?? "?";
            sb.AppendLine($"{node.Name}: actual=({actual?.X ?? double.NaN:F1},{actual?.Y ?? double.NaN:F1}) " +
                          $"expected=({expected.X:F1},{expected.Y:F1}) diff=({dx:F2},{dy:F2}) " +
                          $"container={containerBounds} child={child}({childBounds})");
        }
        return sb.ToString();
    }

    // ---- hit testing ----

    private NodeViewModel? HitTestNode(Point world)
    {
        if (_graph is null) return null;
        NodeViewModel? best = null;
        var bestDistance = (NodeViewModel.Radius + 8) * (NodeViewModel.Radius + 8);
        foreach (var node in _graph.Nodes)
        {
            var dx = world.X - node.X;
            var dy = world.Y - node.Y;
            var distance = dx * dx + dy * dy;
            if (distance <= bestDistance)
            {
                best = node;
                bestDistance = distance;
            }
        }
        return best;
    }

    private EdgeViewModel? HitTestEdge(Point world)
    {
        if (_graph is null) return null;
        foreach (var edge in _graph.Edges)
        {
            var from = new Point(edge.From.X, edge.From.Y);
            var to = new Point(edge.To.X, edge.To.Y);
            if (DistanceToSegment(world, from, to) < 9) return edge;
        }
        return null;
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
        remove.Click += (_, _) => _ = _graph?.RemoveEdgeAsync(edge);
        menu.Items.Add(remove);
        menu.Open(this);
    }

    // ---- helpers for the window / diagnostics ----

    /// <summary>Converts a viewport point to world coordinates.</summary>
    public Point ViewportToWorld(Point viewport) => viewport.Transform(_viewMatrix.Invert());

    /// <summary>Converts a world point to viewport coordinates (diagnostics).</summary>
    public Point WorldToViewport(Point world) => world.Transform(_viewMatrix);

    /// <summary>Centers the current nodes and fits them into the viewport.</summary>
    public void CenterView()
    {
        if (_graph is null || _graph.Nodes.Count == 0) return;
        var minX = _graph.Nodes.Min(n => n.X);
        var maxX = _graph.Nodes.Max(n => n.X);
        var minY = _graph.Nodes.Min(n => n.Y);
        var maxY = _graph.Nodes.Max(n => n.Y);

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