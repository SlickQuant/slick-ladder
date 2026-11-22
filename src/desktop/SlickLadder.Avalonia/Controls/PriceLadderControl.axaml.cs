using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using SlickLadder.Rendering.Core;
using SlickLadder.Rendering.ViewModels;

using AvaloniaThreading = global::Avalonia.Threading;

namespace SlickLadder.Avalonia.Controls;

/// <summary>
/// Avalonia UserControl that hosts the platform-agnostic SkiaRenderer.
/// Thin wrapper (~5% of code) around shared rendering library.
/// </summary>
public partial class PriceLadderControl : UserControl
{
    private SkiaRenderer? _renderer;
    private ViewportManager? _viewport;
    private PriceLadderViewModel? _viewModel;
    private readonly RenderMetrics _metrics;
    private SkiaCanvas? _skiaCanvas;

    public PriceLadderControl()
    {
        InitializeComponent();

        _metrics = new RenderMetrics();
        _renderer = new SkiaRenderer(new RenderConfig());
        _viewport = new ViewportManager();

        // Create and add SkiaCanvas
        _skiaCanvas = new SkiaCanvas(this);
        var host = this.FindControl<ContentControl>("SkiaCanvasHost");
        if (host != null)
        {
            host.Content = _skiaCanvas;
        }

        // Focusable so Space can re-center
        Focusable = true;

        // Listen for DataContext changes
        DataContextChanged += OnDataContextChanged;

        // Setup 60 FPS rendering
        SetupRendering();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void SetupRendering()
    {
        // Request continuous rendering at display refresh rate
        var timer = new System.Timers.Timer(1000.0 / 60.0); // 60 FPS
        timer.Elapsed += (s, e) =>
        {
            AvaloniaThreading.Dispatcher.UIThread.Post(() =>
            {
                // Counted per UI frame, so idle frames that skip the repaint still show in FPS
                _metrics.RecordFrame();

                // Repaint only when the book or view changed: the compositor keeps the last frame
                if (_viewModel?.CurrentSnapshot is { } snapshot && !_renderer!.NeedsRender(snapshot, _viewport!))
                {
                    return;
                }
                _skiaCanvas?.InvalidateVisual();
            }, AvaloniaThreading.DispatcherPriority.Render);
        };
        timer.Start();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is PriceLadderViewModel vm)
        {
            _viewModel = vm;
        }
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

    /// <summary>
    /// Inner Skia canvas control for custom Skia rendering
    /// </summary>
    private class SkiaCanvas : Control
    {
        private readonly PriceLadderControl _parent;

        // Mouse drag state for horizontal scroll
        private bool _isDraggingBar = false;
        private double _dragStartX = 0;

        public SkiaCanvas(PriceLadderControl parent)
        {
            _parent = parent;

            // Input handling
            PointerPressed += OnPointerPressed;
            PointerReleased += OnPointerReleased;
            PointerMoved += OnPointerMoved;
            PointerWheelChanged += OnPointerWheelChanged;
            PointerExited += OnPointerExited;
        }

        public override void Render(DrawingContext context)
        {
            // Use custom drawing operation for Skia rendering
            context.Custom(new SkiaDrawingOperation(Bounds, _parent));
        }

        private class SkiaDrawingOperation : ICustomDrawOperation
        {
            private readonly Rect _bounds;
            private readonly PriceLadderControl _parent;

            public SkiaDrawingOperation(Rect bounds, PriceLadderControl parent)
            {
                _bounds = bounds;
                _parent = parent;
            }

            public void Dispose() { }

            public Rect Bounds => _bounds;

            public bool HitTest(Point p) => _bounds.Contains(p);

            public bool Equals(ICustomDrawOperation? other) => false;

            public void Render(ImmediateDrawingContext context)
            {
                var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
                if (leaseFeature == null) return;

                using var lease = leaseFeature.Lease();
                var canvas = lease?.SkCanvas;
                if (canvas == null) return;

                // Get dimensions
                var width = (int)_bounds.Width;
                var height = (int)_bounds.Height;

                if (_parent._viewport != null)
                {
                    _parent._viewport.Width = width;
                    _parent._viewport.Height = height;
                }

                // If no snapshot yet, clear to background color and wait
                if (_parent._viewModel?.CurrentSnapshot == null)
                {
                    canvas.Clear(new SKColor(15, 15, 15)); // #0f0f0f
                    return;
                }

                var snapshot = _parent._viewModel.CurrentSnapshot.Value;

                if (_parent._metrics.TraceRenderTimings)
                {
                    var start = Stopwatch.GetTimestamp();
                    _parent._renderer!.Render(canvas, snapshot, _parent._viewport!);
                    _parent._metrics.RecordRenderTime(Stopwatch.GetTimestamp() - start, in snapshot);
                }
                else
                {
                    // Use shared renderer (supports scrolling)
                    _parent._renderer!.Render(canvas, snapshot, _parent._viewport!);
                }
            }
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            _parent.Focus();

            var pos = e.GetPosition(this);
            var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;

            // Middle button (wheel click): re-center, unless it lands on a qty or Work cell and the
            // app handles the click
            if (kind == PointerUpdateKind.MiddleButtonPressed)
            {
                if (!RaiseCellClick(pos, ClickButton.Middle))
                {
                    _parent.Recenter();
                }
                e.Handled = true;
                return;
            }

            if (_parent._viewport == null || _parent._viewModel == null || _parent._viewModel.CurrentSnapshot == null) return;

            var x = (float)pos.X;

            // Check if click is in bar column for horizontal scroll dragging
            if (_parent._viewport.OrdersColumnX.HasValue)
            {
                var barStartX = _parent._viewport.OrdersColumnX.Value;
                var barColumnWidth = _parent._viewport.OrdersColumnWidth;

                if (x >= barStartX && x < barStartX + barColumnWidth)
                {
                    _isDraggingBar = true;
                    _dragStartX = pos.X;
                    e.Handled = true;
                    return;
                }
            }

            // Back/forward buttons don't click cells
            if (kind is PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.RightButtonPressed)
            {
                RaiseCellClick(pos, kind == PointerUpdateKind.LeftButtonPressed ? ClickButton.Left : ClickButton.Right);
            }
        }

        /// <summary>
        /// Raise a level click on a qty cell or a Work click on a Work cell. Returns true when the app handled it
        /// </summary>
        private bool RaiseCellClick(Point pos, ClickButton button) =>
            _parent._viewport != null && _parent._viewModel != null &&
            _parent._viewModel.HandleClick(_parent._viewport, (float)pos.X, (float)pos.Y, button);

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_isDraggingBar)
            {
                _isDraggingBar = false;
                e.Handled = true;
            }
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_parent._renderer == null) return;

            // Handle horizontal scroll dragging
            if (_isDraggingBar)
            {
                var pos = e.GetPosition(this);
                var dragDelta = _dragStartX - pos.X; // Invert for natural scroll direction
                _parent._renderer.AdjustHorizontalScroll(dragDelta);
                _dragStartX = pos.X;
                e.Handled = true;
                return;
            }

            // Mouse move tracking can be added later when needed
            // For now, matches minimal web version functionality
        }

        private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (_parent._viewport == null || _parent._viewModel == null || _parent._renderer == null) return;

            // Shift+Scroll: Adjust segment scale globally
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                var scaleDelta = e.Delta.Y > 0 ? 1 : -1; // Inverted for intuitive zoom
                if (_parent._viewModel.CurrentSnapshot.HasValue)
                {
                    var snapshot = _parent._viewModel.CurrentSnapshot.Value;
                    _parent._renderer.AdjustSegmentScale(scaleDelta, snapshot, _parent._viewport);
                }
                e.Handled = true;
                return;
            }

            // Auto-center pins the view, so vertical scroll is disabled. Still consume the wheel
            // so a parent ScrollViewer doesn't scroll (matches preventDefault on web)
            if (_parent._viewport.AutoCenter)
            {
                e.Handled = true;
                return;
            }

            var delta = e.Delta.Y;

            if (_parent._viewport.RemovalMode == LevelRemovalMode.RemoveRow)
            {
                // DENSE PACKING MODE: Row-based scrolling
                // Scroll up (Delta > 0) = show higher prices (decrease offset)
                // Scroll down (Delta < 0) = show lower prices (increase offset)
                var scrollTicks = delta > 0 ? -5 : 5; // Negative because we want to decrease offset for higher prices
                _parent._viewport.DensePackingScrollOffset += scrollTicks;
            }
            else
            {
                // PRICE-TO-ROW MAPPING MODE: Price-based scrolling
                // Each tick moves 5 price levels based on current tick size
                var scrollTicks = delta > 0 ? 5 : -5;
                var scrollAmount = scrollTicks * _parent._viewport.TickSize;

                // Initialize center price from mid price on first scroll (if not set)
                if (_parent._viewport.CenterPrice == 0 && _parent._viewModel.CurrentSnapshot.HasValue)
                {
                    var snapshot = _parent._viewModel.CurrentSnapshot.Value;
                    if (snapshot.Bids.Length > 0 && snapshot.Asks.Length > 0)
                    {
                        _parent._viewport.CenterPrice = (snapshot.Bids[0].Price + snapshot.Asks[0].Price) / 2m;
                    }
                }

                // Scroll relative to current viewport center
                var newCenterPrice = _parent._viewport.CenterPrice + scrollAmount;
                _parent._viewport.CenterPrice = newCenterPrice;
            }

            // Don't request new snapshot - just let the renderer use the viewport scroll state
        }

        private void OnPointerExited(object? sender, PointerEventArgs e)
        {
            // Stop dragging if pointer leaves canvas
            if (_isDraggingBar)
            {
                _isDraggingBar = false;
            }

            // Mouse leave tracking can be added later when needed
        }
    }
}
