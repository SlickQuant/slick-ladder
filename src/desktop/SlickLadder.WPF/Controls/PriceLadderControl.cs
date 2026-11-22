using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;
using SlickLadder.Rendering.Core;
using SlickLadder.Rendering.ViewModels;

namespace SlickLadder.WPF.Controls;

/// <summary>
/// WPF UserControl that hosts the platform-agnostic SkiaRenderer.
/// Thin wrapper (~5% of code) around shared rendering library.
/// </summary>
public partial class PriceLadderControl : UserControl
{
    private SkiaRenderer? _renderer;
    private ViewportManager? _viewport;
    private PriceLadderViewModel? _viewModel;
    private readonly RenderMetrics _metrics;

    // Mouse drag state for horizontal scroll
    private bool _isDraggingBar = false;
    private double _dragStartX = 0;

    public PriceLadderControl()
    {
        InitializeComponent();

        _metrics = new RenderMetrics();
        _renderer = new SkiaRenderer(new RenderConfig());
        // _renderer = new SkiaRenderer(new RenderConfig { 
        //     DebugDirtyRows = true,
        //     DirtyRowOverlayColor = new SKColor(255, 255, 0, 80)
        //  });
        _viewport = new ViewportManager();

        // 60 FPS rendering via CompositionTarget
        CompositionTarget.Rendering += OnCompositionTargetRendering;

        // Input handling
        SkiaCanvas.MouseDown += OnMouseDown;
        SkiaCanvas.MouseUp += OnMouseUp;
        SkiaCanvas.MouseMove += OnMouseMove;
        SkiaCanvas.MouseWheel += OnMouseWheel;
        SkiaCanvas.MouseLeave += OnMouseLeave;

        // Focusable so Space can re-center. The default focus visual shows for keyboard focus only
        Focusable = true;

        // Listen for DataContext changes
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is PriceLadderViewModel vm)
        {
            _viewModel = vm;
        }
    }

    private void OnCompositionTargetRendering(object? sender, EventArgs e)
    {
        // Counted per UI frame, so idle frames that skip the repaint still show in FPS
        _metrics.RecordFrame();

        // Repaint at display refresh rate (60 FPS), but only when the book or view changed:
        // WPF keeps showing the last frame, so idle ladders cost nothing
        if (_viewModel?.CurrentSnapshot is { } snapshot && !_renderer!.NeedsRender(snapshot, _viewport!))
        {
            return;
        }
        SkiaCanvas?.InvalidateVisual();
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        _viewport!.Width = e.Info.Width;
        _viewport.Height = e.Info.Height;

        // If no snapshot yet, clear to background color and wait
        if (_viewModel?.CurrentSnapshot == null)
        {
            canvas.Clear(new SkiaSharp.SKColor(15, 15, 15)); // #0f0f0f
            _renderer!.Invalidate(); // The clear wiped the frame the renderer left in the surface
            return;
        }

        var snapshot = _viewModel.CurrentSnapshot.Value;

        // SKElement keeps its bitmap between paints and only this control draws into it, so the
        // renderer updates changed rows in place instead of copying a cached frame
        if (_metrics.TraceRenderTimings)
        {
            var start = Stopwatch.GetTimestamp();
            _renderer!.RenderRetained(e.Surface, snapshot, _viewport);
            _metrics.RecordRenderTime(Stopwatch.GetTimestamp() - start, in snapshot);
        }
        else
        {
            _renderer!.RenderRetained(e.Surface, snapshot, _viewport);
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();

        var pos = e.GetPosition(SkiaCanvas);

        // Middle button (wheel click): re-center, unless it lands on a qty or Work cell and the app
        // handles the click
        if (e.ChangedButton == MouseButton.Middle)
        {
            if (!RaiseCellClick(pos, ClickButton.Middle))
            {
                Recenter();
            }
            e.Handled = true;
            return;
        }

        if (_viewport == null || _viewModel == null || _viewModel.CurrentSnapshot == null) return;

        var x = (float)pos.X;

        // Check if click is in bar column for horizontal scroll dragging
        if (_viewport.OrdersColumnX.HasValue)
        {
            var barStartX = _viewport.OrdersColumnX.Value;
            var barColumnWidth = _viewport.OrdersColumnWidth;

            if (x >= barStartX && x < barStartX + barColumnWidth)
            {
                _isDraggingBar = true;
                _dragStartX = pos.X;
                SkiaCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        // Back/forward buttons don't click cells
        if (e.ChangedButton is MouseButton.Left or MouseButton.Right)
        {
            RaiseCellClick(pos, e.ChangedButton == MouseButton.Left ? ClickButton.Left : ClickButton.Right);
        }
    }

    /// <summary>
    /// Raise a level click on a qty cell or a Work click on a Work cell. Returns true when the app handled it
    /// </summary>
    private bool RaiseCellClick(Point pos, ClickButton button) =>
        _viewport != null && _viewModel != null && _viewModel.HandleClick(_viewport, (float)pos.X, (float)pos.Y, button);

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingBar)
        {
            _isDraggingBar = false;
            SkiaCanvas.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_renderer == null) return;

        // Handle horizontal scroll dragging
        if (_isDraggingBar)
        {
            var pos = e.GetPosition(SkiaCanvas);
            var dragDelta = _dragStartX - pos.X; // Invert for natural scroll direction
            _renderer.AdjustHorizontalScroll(dragDelta);
            _dragStartX = pos.X;
            e.Handled = true;
            return;
        }

        // Mouse move tracking can be added later when needed
        // For now, matches minimal web version functionality
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_viewport == null || _viewModel == null || _renderer == null) return;

        // Shift+Scroll: Adjust segment scale globally
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            var delta = e.Delta > 0 ? 1 : -1; // Inverted for intuitive zoom
            if (_viewModel.CurrentSnapshot.HasValue)
            {
                var snapshot = _viewModel.CurrentSnapshot.Value;
                _renderer.AdjustSegmentScale(delta, snapshot, _viewport);
            }
            e.Handled = true;
            return;
        }

        // Auto-center pins the view, so vertical scroll is disabled. Still consume the wheel
        // so a parent ScrollViewer doesn't scroll (matches preventDefault on web)
        if (_viewport.AutoCenter)
        {
            e.Handled = true;
            return;
        }

        if (_viewport.RemovalMode == LevelRemovalMode.RemoveRow)
        {
            // DENSE PACKING MODE: Row-based scrolling
            // Scroll up (Delta > 0) = show higher prices (decrease offset)
            // Scroll down (Delta < 0) = show lower prices (increase offset)
            var scrollTicks = e.Delta > 0 ? -5 : 5; // Negative because we want to decrease offset for higher prices
            _viewport.DensePackingScrollOffset += scrollTicks;
        }
        else
        {
            // PRICE-TO-ROW MAPPING MODE: Price-based scrolling
            // Each tick moves 5 price levels based on current tick size
            var scrollTicks = e.Delta > 0 ? 5 : -5;
            var scrollAmount = scrollTicks * _viewport.TickSize;

            // Initialize center price from mid price on first scroll (if not set)
            if (_viewport.CenterPrice == 0 && _viewModel.CurrentSnapshot.HasValue)
            {
                var snapshot = _viewModel.CurrentSnapshot.Value;
                if (snapshot.Bids.Length > 0 && snapshot.Asks.Length > 0)
                {
                    _viewport.CenterPrice = (snapshot.Bids[0].Price + snapshot.Asks[0].Price) / 2m;
                }
            }

            // Scroll relative to current viewport center
            var newCenterPrice = _viewport.CenterPrice + scrollAmount;
            _viewport.CenterPrice = newCenterPrice;
        }

        // Don't request new snapshot - just let the renderer use the viewport scroll state
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        // Stop dragging if mouse leaves canvas
        if (_isDraggingBar)
        {
            _isDraggingBar = false;
            SkiaCanvas.ReleaseMouseCapture();
        }

        // Mouse leave tracking can be added later when needed
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            Recenter();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    /// <summary>
    /// Move the bid/ask midpoint to the middle of the ladder once
    /// </summary>
    public void Recenter()
    {
        if (_viewport != null && _viewModel?.CurrentSnapshot is { } snapshot)
        {
            _viewport.Recenter(snapshot);
        }
    }

    /// <summary>
    /// Get access to render metrics (for demo UI)
    /// </summary>
    public RenderMetrics GetMetrics() => _metrics;

    /// <summary>
    /// Get access to viewport manager (for demo UI)
    /// </summary>
    public ViewportManager? GetViewport() => _viewport;

    public void SetMboOrderSizeFilter(long filter)
    {
        _renderer?.SetMboOrderSizeFilter(filter);
    }
}
