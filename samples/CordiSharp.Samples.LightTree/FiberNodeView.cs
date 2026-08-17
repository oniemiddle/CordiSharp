using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;

namespace CordiSharp.Samples.LightTree;

/// <summary>
/// A node "light" in the tree: a circle whose fill follows the fiber state with a
/// 0.3 s <see cref="BrushTransition"/>, plus the plugin name, the state label and a
/// warning badge. Plain left button drags the node; Ctrl+left click raises
/// <see cref="CtrlClicked"/> (connect / cancel-connect). Right-click context menu
/// is attached by <see cref="GraphCanvas"/>.
/// </summary>
public sealed class FiberNodeView : Border
{
    public const double Radius = NodeViewModel.Radius;
    private const double Inset = 8;
    private const double Size = Radius * 2 + Inset * 2;

    private readonly NodeViewModel _node;
    private Ellipse _ellipse = null!;
    private TextBlock _stateText = null!;
    private Border _badge = null!;

    private static readonly IBrush StrokeNormal = new SolidColorBrush(Color.Parse("#37474F"));
    private static readonly IBrush StrokeLink = new SolidColorBrush(Color.Parse("#EF6C00"));

    private Point _pressWorld;
    private double _startX;
    private double _startY;
    private bool _dragging;
    private bool _ctrlPress;
    private Point _ctrlPressWorld;

    /// <summary>The world canvas this view lives in (for coordinate conversion).</summary>
    public Canvas? WorldCanvas { get; set; }

    /// <summary>Raised on Ctrl+left click (connect / cancel-connect selection).
    /// Plain left button only drags the node — it never changes selection state.</summary>
    public event Action<NodeViewModel>? CtrlClicked;

    public FiberNodeView(NodeViewModel node)
    {
        _node = node;
        Width = Size;
        Height = Size;
        Background = Brushes.Transparent;
        Child = BuildContent();

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
    }

    private Canvas BuildContent()
    {
        var canvas = new Canvas { Width = Size, Height = Size };

        _ellipse = new Ellipse
        {
            Width = Radius * 2,
            Height = Radius * 2,
            Stroke = StrokeNormal,
            StrokeThickness = 2,
            Fill = _node.StateBrush,
            Transitions =
            [
                new BrushTransition
                {
                    Property = Shape.FillProperty,
                    Duration = TimeSpan.FromMilliseconds(300),
                }

            ]
        };
        Canvas.SetLeft(_ellipse, Inset);
        Canvas.SetTop(_ellipse, Inset);

        var label = new TextBlock
        {
            Text = _node.Name,
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            IsHitTestVisible = false,
        };
        // a Border host centers the label inside the circle (TextBlock has no
        // vertical content alignment of its own)
        var labelHost = new Border
        {
            Width = Radius * 2,
            Height = Radius * 2,
            IsHitTestVisible = false,
            Child = label,
        };
        Canvas.SetLeft(labelHost, Inset);
        Canvas.SetTop(labelHost, Inset);

        _stateText = new TextBlock
        {
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.Parse("#607D8B")),
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false,
            Width = Radius * 2 + 16,
            Height = 16,
        };
        Canvas.SetLeft(_stateText, Inset - 8);
        Canvas.SetTop(_stateText, Radius * 2 + Inset + 2);

        _badge = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#B71C1C")),
            CornerRadius = new CornerRadius(8),
            Width = 16,
            Height = 16,
            IsVisible = false,
            Child = new TextBlock
            {
                Text = "!",
                FontSize = 10,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            },
        };
        Canvas.SetRight(_badge, 0);
        Canvas.SetTop(_badge, 0);

        canvas.Children.Add(_ellipse);
        canvas.Children.Add(labelHost);
        canvas.Children.Add(_stateText);
        canvas.Children.Add(_badge);
        return canvas;
    }

    /// <summary>Re-reads node visuals (state color / warning / link-source highlight).</summary>
    public void Refresh()
    {
        _ellipse.Fill = _node.StateBrush;
        _ellipse.Stroke = _node.IsLinkSource ? StrokeLink : StrokeNormal;
        _ellipse.StrokeThickness = _node.IsLinkSource ? 3.5 : 2;
        _stateText.Text = StateColors.Text(_node.State);
        _badge.IsVisible = _node.IsWarning;
        ToolTip.SetTip(this,
            $"{_node.Name}\n状态: {StateColors.Text(_node.State)}\n" +
            $"依赖: {(string.Join(", ", _node.ProviderNames) is { Length: > 0 } deps ? deps : "(无)")}\n" +
            "说明: 拖拽移动 · 右键切换状态 · 滚轮缩放画布");
    }

    // ---- pointer interaction ----
    // Plain left button: drag to move only. Ctrl+left click: connect / cancel-connect.

    private Visual GetOrigin() => (Visual?)WorldCanvas ?? this;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            // Ctrl+click: selection/connection — record it, do not drag
            _ctrlPress = true;
            _ctrlPressWorld = e.GetPosition(GetOrigin());
            e.Handled = true;
            return;
        }
        _ctrlPress = false;

        _dragging = true;
        _pressWorld = e.GetPosition(GetOrigin());
        _startX = _node.X;
        _startY = _node.Y;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        var current = e.GetPosition(GetOrigin());
        var dx = current.X - _pressWorld.X;
        var dy = current.Y - _pressWorld.Y;
        _node.X = Math.Max(0, _startX + dx);
        _node.Y = Math.Max(0, _startY + dy);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_ctrlPress)
        {
            _ctrlPress = false;
            var released = e.GetPosition(GetOrigin());
            var dx = released.X - _ctrlPressWorld.X;
            var dy = released.Y - _ctrlPressWorld.Y;
            if (Math.Sqrt(dx * dx + dy * dy) < 4) CtrlClicked?.Invoke(_node);
            e.Handled = true;
            return;
        }

        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
        // plain left click that did not move does NOT change selection state
    }
}
