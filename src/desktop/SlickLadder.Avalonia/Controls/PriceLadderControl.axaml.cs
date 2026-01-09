using System;
using System.Linq;
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

    /// <summary>
    /// Get access to render metrics (for demo UI)
    /// </summary>
    public RenderMetrics GetMetrics() => _metrics;

    /// <summary>
    /// Get access to viewport manager (for demo UI)
    /// </summary>
    public ViewportManager? GetViewport() => _viewport;

    /// <summary>
    /// Inner Skia canvas control for custom Skia rendering
    /// </summary>
    private class SkiaCanvas : Control
    {
        private readonly PriceLadderControl _parent;

        public SkiaCanvas(PriceLadderControl parent)
        {
            _parent = parent;

            // Input handling
            PointerPressed += OnPointerPressed;
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

                // Use shared renderer (supports scrolling)
                _parent._renderer!.Render(canvas, _parent._viewModel.CurrentSnapshot.Value, _parent._viewport!);

                // Track FPS
                _parent._metrics.RecordFrame();
            }
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_parent._viewport == null || _parent._viewModel == null || _parent._viewModel.CurrentSnapshot == null) return;

            var pos = e.GetPosition(this);
            var snapshot = _parent._viewModel.CurrentSnapshot.Value;

            // Convert screen Y to row index
            var rowIndex = (int)(pos.Y / RenderConfig.RowHeight);
            var visibleRows = _parent._viewport.Height / RenderConfig.RowHeight;
            var midRow = visibleRows / 2;

            // Determine reference price (viewport center or mid market)
            var referencePrice = _parent._viewport.CenterPrice != 0
                ? _parent._viewport.CenterPrice
                : (snapshot.MidPrice ?? 50000m);

            // Convert row to price using price-to-row mapping (reverse calculation)
            var tickSize = _parent._viewport.TickSize;
            var rowOffset = rowIndex - midRow;
            var price = referencePrice - (rowOffset * tickSize); // Negative rowOffset because higher price = lower row

            // Find the level at this price in the snapshot
            decimal? clickedPrice = null;
            SlickLadder.Core.Models.Side side;

            // Check if it's an ask level
            var askLevel = snapshot.Asks.FirstOrDefault(a => Math.Abs(a.Price - price) < 0.005m);
            if (askLevel.Price != 0)
            {
                clickedPrice = askLevel.Price;
                side = SlickLadder.Core.Models.Side.ASK;
            }
            else
            {
                // Check if it's a bid level
                var bidLevel = snapshot.Bids.FirstOrDefault(b => Math.Abs(b.Price - price) < 0.005m);
                if (bidLevel.Price != 0)
                {
                    clickedPrice = bidLevel.Price;
                    side = SlickLadder.Core.Models.Side.BID;
                }
                else
                {
                    return; // Clicked on empty row
                }
            }

            if (clickedPrice.HasValue)
            {
                // Notify ViewModel of click
                _parent._viewModel.HandlePriceClick(clickedPrice.Value, side);
            }
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            // Mouse move tracking can be added later when needed
            // For now, matches minimal web version functionality
        }

        private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (_parent._viewport == null || _parent._viewModel == null) return;

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
            // Mouse leave tracking can be added later when needed
        }
    }
}
