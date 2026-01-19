using System.Diagnostics;
using System.Linq;
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

        var snapshot = _viewModel.CurrentSnapshot.Value;

        if (_metrics.TraceRenderTimings)
        {
            var start = Stopwatch.GetTimestamp();
            _renderer!.Render(canvas, snapshot, _viewport);
            _metrics.RecordRenderTime(Stopwatch.GetTimestamp() - start, in snapshot);
        }
        else
        {
            // Use shared renderer (no scrolling support - matches web version)
            _renderer!.Render(canvas, snapshot, _viewport);
        }

        // Track FPS
        _metrics.RecordFrame();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewport == null || _viewModel == null || _viewModel.CurrentSnapshot == null) return;

        var pos = e.GetPosition(SkiaCanvas);
        var snapshot = _viewModel.CurrentSnapshot.Value;
        var x = (float)pos.X;

        // Check if click is in bar column for horizontal scroll dragging
        if (_viewport.VolumeBarColumnX.HasValue)
        {
            var barStartX = _viewport.VolumeBarColumnX.Value;
            var barColumnWidth = _viewport.VolumeBarMaxWidth;

            if (x >= barStartX && x < barStartX + barColumnWidth)
            {
                _isDraggingBar = true;
                _dragStartX = pos.X;
                SkiaCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        // Use viewport's column X positions (respects ShowOrderCount setting)
        var columnWidth = _viewport.ColumnWidth;
        var bidQtyColumnX = _viewport.BidQtyColumnX;
        var askQtyColumnX = _viewport.AskQtyColumnX;

        // Check if click is within bid qty or ask qty column
        bool clickedBidQty = x >= bidQtyColumnX && x < bidQtyColumnX + columnWidth;
        bool clickedAskQty = x >= askQtyColumnX && x < askQtyColumnX + columnWidth;

        if (!clickedBidQty && !clickedAskQty)
        {
            return; // Clicked outside quantity columns
        }

        var visibleRows = _viewport.Height / RenderConfig.RowHeight;
        var midRow = visibleRows / 2;

        System.Diagnostics.Debug.WriteLine($"Viewport: height={_viewport.Height}, visibleRows={visibleRows}, midRow={midRow}");

        decimal clickedPrice;

        if (_viewport.RemovalMode == LevelRemovalMode.RemoveRow)
        {
            // Dense packing mode: asks and bids are rendered consecutively
            // Build the same layout the renderer uses
            var nonEmptyAsks = snapshot.Asks.Where(l => l.Quantity > 0).ToArray();
            var nonEmptyBids = snapshot.Bids.Where(l => l.Quantity > 0).ToArray();
            var scrollOffset = _viewport.DensePackingScrollOffset;
            var totalLevels = nonEmptyAsks.Length + nonEmptyBids.Length;

            var virtualTopRow = nonEmptyAsks.Length - midRow + scrollOffset;

            int startAskIndex, askRowsToRender;
            int startBidIndex, bidRowsToRender;
            float topOffset = 0;

            if (virtualTopRow < 0)
            {
                topOffset = -virtualTopRow * RenderConfig.RowHeight;
                startAskIndex = 0;
                askRowsToRender = Math.Min(nonEmptyAsks.Length, Math.Max(0, visibleRows + virtualTopRow));
                startBidIndex = 0;
                var bidRowsAvailable = Math.Max(0, visibleRows + virtualTopRow - nonEmptyAsks.Length);
                bidRowsToRender = Math.Min(nonEmptyBids.Length, bidRowsAvailable);
            }
            else if (virtualTopRow < nonEmptyAsks.Length)
            {
                startAskIndex = virtualTopRow;
                askRowsToRender = Math.Min(nonEmptyAsks.Length - startAskIndex, visibleRows);
                startBidIndex = 0;
                var bidRowsAvailable = visibleRows - askRowsToRender;
                bidRowsToRender = Math.Min(nonEmptyBids.Length, bidRowsAvailable);
            }
            else if (virtualTopRow < totalLevels)
            {
                startAskIndex = nonEmptyAsks.Length;
                askRowsToRender = 0;
                var bidStartRow = virtualTopRow - nonEmptyAsks.Length;
                startBidIndex = bidStartRow;
                bidRowsToRender = Math.Min(nonEmptyBids.Length - startBidIndex, visibleRows);
            }
            else
            {
                return; // Scrolled past all data
            }

            var firstRowIndex = (int)(topOffset / RenderConfig.RowHeight);

            // Debug: calculate which row the best ask (asks[0]) should render at
            // Formula: askIndex = nonEmptyAsks.Length - 1 - startAskIndex - i
            // For askIndex=0: i = nonEmptyAsks.Length - 1 - startAskIndex
            var bestAskLoopIndex = nonEmptyAsks.Length - 1 - startAskIndex;
            var bestAskRowIndex = bestAskLoopIndex >= 0 && bestAskLoopIndex < askRowsToRender
                ? firstRowIndex + bestAskLoopIndex
                : -1;
            var lastAskRow = firstRowIndex + askRowsToRender - 1;
            var bestAskExpectedY = topOffset + (bestAskLoopIndex * RenderConfig.RowHeight);
            var bestAskYRange = $"{bestAskExpectedY}-{bestAskExpectedY + RenderConfig.RowHeight - 1}";
            System.Diagnostics.Debug.WriteLine($"Layout: bestAsk (asks[0]) should render at i={bestAskLoopIndex}, rowIndex={bestAskRowIndex} (Y={bestAskYRange}, lastAskRow={lastAskRow})");

            // Convert click Y to row index, then to relative row
            // IMPORTANT: Subtract one row height to correct for coordinate system offset
            // Empirically, clicks land exactly one row (24px) below the intended target
            // This may be due to grid line rendering, text baseline positioning, or WPF coordinate system
            var clickY = (float)pos.Y;
            var adjustedY = Math.Max(0, clickY - RenderConfig.RowHeight);
            var rowIndex = (int)(adjustedY / RenderConfig.RowHeight);
            var relativeRow = rowIndex - firstRowIndex;

            System.Diagnostics.Debug.WriteLine($"Dense Click Debug: clickY={clickY}, topOffset={topOffset}, rowIndex={rowIndex}, firstRowIndex={firstRowIndex}, relativeRow={relativeRow}, askRows={askRowsToRender}, startAskIdx={startAskIndex}");

            // Debug: what price is at row 17 (first bid)?
            if (rowIndex == 17 && relativeRow >= askRowsToRender)
            {
                var bidRow = relativeRow - askRowsToRender;
                var bidIndex = nonEmptyBids.Length - 1 - startBidIndex - bidRow;
                if (bidIndex >= 0 && bidIndex < nonEmptyBids.Length)
                {
                    System.Diagnostics.Debug.WriteLine($"WARNING: Row 17 contains FIRST BID at price={nonEmptyBids[bidIndex].Price}, but best ask is at row 16!");
                }
            }

            if (relativeRow < 0)
            {
                return; // Clicked above visible data
            }

            if (relativeRow < askRowsToRender)
            {
                // Clicked on an ask row
                var askIndex = nonEmptyAsks.Length - 1 - startAskIndex - relativeRow;
                var expectedRenderY = topOffset + (relativeRow * RenderConfig.RowHeight);
                System.Diagnostics.Debug.WriteLine($"Ask click: askIndex={askIndex}, nonEmptyAsks.Length={nonEmptyAsks.Length}, expectedRenderY={expectedRenderY}");
                if (askIndex < 0 || askIndex >= nonEmptyAsks.Length)
                {
                    return;
                }
                var level = nonEmptyAsks[askIndex];
                System.Diagnostics.Debug.WriteLine($"Selected ask: price={level.Price}, qty={level.Quantity}");
                clickedPrice = level.Price;
            }
            else
            {
                // Clicked on a bid row
                var bidRow = relativeRow - askRowsToRender;
                if (bidRow >= bidRowsToRender)
                {
                    return; // Clicked below visible data
                }
                var bidIndex = nonEmptyBids.Length - 1 - startBidIndex - bidRow;
                if (bidIndex < 0 || bidIndex >= nonEmptyBids.Length)
                {
                    return;
                }
                var level = nonEmptyBids[bidIndex];
                clickedPrice = level.Price;
            }
        }
        else
        {
            // ShowEmpty mode: price-to-row mapping
            var rowIndex = (int)(pos.Y / RenderConfig.RowHeight);
            var tickSize = _viewport.TickSize;
            var referencePrice = _viewport.CenterPrice != 0
                ? _viewport.CenterPrice
                : (snapshot.MidPrice ?? 50000m);

            var rowOffset = rowIndex - midRow;
            var price = referencePrice - (rowOffset * tickSize);
            price = Math.Round(price / tickSize) * tickSize;

            // Find the level at this price
            var askLevel = snapshot.Asks.FirstOrDefault(a => Math.Abs(a.Price - price) < tickSize * 0.5m);
            if (askLevel.Price != 0 && askLevel.Quantity > 0)
            {
                clickedPrice = askLevel.Price;
            }
            else
            {
                var bidLevel = snapshot.Bids.FirstOrDefault(b => Math.Abs(b.Price - price) < tickSize * 0.5m);
                if (bidLevel.Price != 0 && bidLevel.Quantity > 0)
                {
                    clickedPrice = bidLevel.Price;
                }
                else
                {
                    return; // Clicked on empty row
                }
            }
        }

        // Determine trade side based on which column was clicked
        // Click on BID qty column = BUY (Side.ASK)
        // Click on ASK qty column = SELL (Side.BID)
        var tradeSide = clickedBidQty
            ? SlickLadder.Core.Models.Side.ASK
            : SlickLadder.Core.Models.Side.BID;

        // Notify ViewModel of click with price, side
        _viewModel.HandlePriceClick(clickedPrice, tradeSide);
    }

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
