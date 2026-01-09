using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    public PriceLadderControl()
    {
        InitializeComponent();

        _metrics = new RenderMetrics();
        _renderer = new SkiaRenderer(new RenderConfig());
        _viewport = new ViewportManager();

        // 60 FPS rendering via CompositionTarget
        CompositionTarget.Rendering += OnCompositionTargetRendering;

        // Input handling
        SkiaCanvas.MouseDown += OnMouseDown;
        SkiaCanvas.MouseMove += OnMouseMove;
        SkiaCanvas.MouseWheel += OnMouseWheel;
        SkiaCanvas.MouseLeave += OnMouseLeave;

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
        // Trigger repaint at display refresh rate (60 FPS)
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
            return;
        }

        // Use shared renderer (no scrolling support - matches web version)
        _renderer!.Render(canvas, _viewModel.CurrentSnapshot.Value, _viewport);

        // Track FPS
        _metrics.RecordFrame();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewport == null || _viewModel == null || _viewModel.CurrentSnapshot == null) return;

        var pos = e.GetPosition(SkiaCanvas);
        var snapshot = _viewModel.CurrentSnapshot.Value;

        // Convert screen Y to row index
        var rowIndex = (int)(pos.Y / RenderConfig.RowHeight);
        var visibleRows = _viewport.Height / RenderConfig.RowHeight;
        var midRow = visibleRows / 2;

        // Determine reference price (viewport center or mid market)
        var referencePrice = _viewport.CenterPrice != 0
            ? _viewport.CenterPrice
            : (snapshot.MidPrice ?? 50000m);

        // Convert row to price using price-to-row mapping (reverse calculation)
        var tickSize = _viewport.TickSize;
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
            _viewModel.HandlePriceClick(clickedPrice.Value, side);
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        // Mouse move tracking can be added later when needed
        // For now, matches minimal web version functionality
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_viewport == null || _viewModel == null) return;

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
        // Mouse leave tracking can be added later when needed
    }

    /// <summary>
    /// Get access to render metrics (for demo UI)
    /// </summary>
    public RenderMetrics GetMetrics() => _metrics;

    /// <summary>
    /// Get access to viewport manager (for demo UI)
    /// </summary>
    public ViewportManager? GetViewport() => _viewport;
}
