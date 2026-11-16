using SkiaSharp;
using SlickLadder.Core;
using SlickLadder.Core.Managers;
using SlickLadder.Core.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace SlickLadder.Rendering.Core;

/// <summary>
/// Platform-agnostic SkiaSharp renderer that ports canvas-renderer.ts logic.
/// Works on both WPF and Avalonia with zero allocation during rendering.
/// IMPORTANT: Column layout MUST match canvas-renderer.ts exactly!
/// </summary>
public class SkiaRenderer : IDisposable
{
    // Reusable paint objects (allocated once for zero-allocation rendering)
    private readonly SKPaint _bidBackgroundPaint;
    private readonly SKPaint _askBackgroundPaint;
    private readonly SKPaint _priceBackgroundPaint;
    private readonly SKPaint _orderCountBackgroundPaint;
    private readonly SKPaint _backgroundPaint;
    private readonly SKPaint _textPaint;
    private readonly SKPaint _bidVolumePaint;
    private readonly SKPaint _askVolumePaint;
    private readonly SKPaint _gridLinePaint;
    private readonly SKPaint _highlightPaint;
    private readonly SKPaint _dirtyRowPaint;
    private readonly SKPaint _ownOrderBorderPaint;
    private readonly SKPaint _segmentTextPaint;
    private readonly TextBlobCache _textBlobs;
    private readonly TextBlobCache _segmentTextBlobs;
    private readonly HashSet<int> _debugDirtyRows = new HashSet<int>();
    private decimal _priceFormatTickSize; // Display tick size _priceFormat was built for
    private string _priceFormat = "F2";
    private decimal _quantityTextThreshold; // MinQuantityThreshold _quantityTexts were made with
    private readonly TextCache<decimal> _priceTexts;
    private readonly TextCache<decimal> _quantityTexts;
    private readonly TextCache<int> _orderCountTexts = new(count => $"({count})");
    private bool _debugOverlayAllRows;

    private readonly RenderConfig _config;
    private SKSurface? _cachedSurface; // Raster, or a texture on the target's GPU
    private SKImage? _cachedImage; // Raster cache: wraps its pixels (no copy), remade whenever they change
    // GPU of the target the cache was made for, zero for raster. A handle, not the wrapper, so the
    // finalizer can read it after the wrapper's own finalizer ran
    private IntPtr _cachedFor;
    private int _cachedThread; // Thread that made the cache, the only one that may free a GPU cache

    // GPU caches of renderers disposed off their render thread or never disposed, freed by the next
    // Render on that GPU
    private static readonly ConcurrentQueue<(IntPtr Context, SKSurface Surface)> s_gpuCachesToFree = new();
    private int _cachedWidth;
    private int _cachedHeight;
    private SKSurface? _surface; // Surface being rendered (the cache or a retained target), during RenderRows
    private readonly SKPixmap _pixmap = new(); // _surface's pixels during RenderRows (raster only, see RenderRetained)
    private bool _onGpu; // _surface is a GPU texture: no pixels to copy, rows are drawn instead
    private (IntPtr Address, int Width, int Height, int RowBytes) _rowsPixels; // Pixels _rows describes
    private RowState[] _rowsBeforeMove = Array.Empty<RowState>(); // MoveRows on a GPU surface
    private bool _needsFullRedraw = true;
    private bool _pixelsChanged; // RenderRows wrote pixels this call
    private decimal _currentDisplayTickSize = 0.01m; // Set at start of each Render() call

    // What the cache shows: a state per row, the layout they were drawn under and the reference
    // price (Show Empty Rows) that mapped prices to rows. _nextRows is the frame being built
    private RowState[] _rows = Array.Empty<RowState>();
    private RowState[] _nextRows = Array.Empty<RowState>();
    private LayoutState _lastLayout;
    private decimal _lastReferencePrice;

    private const int MaxRowMove = 4; // How far FindMovedRows looks for a row's previous position
    private byte[] _rowTemplate = Array.Empty<byte>(); // Blank row strip, see EnsureRowTemplate
    private int _rowTemplateRowBytes;
    private SKImageInfo _rowTemplateInfo;
    private LayoutState _rowTemplateLayout;

    // Last Render input, for NeedsRender
    private OrderBookSnapshot _lastSnapshot;
    private ViewState _lastView;
    private float _lastBarMaxWidth; // For AdjustHorizontalScroll's limit

    // Segment rendering state (mirrors TypeScript SegmentRenderState)
    private SegmentRenderState _segmentState = new SegmentRenderState();
    private readonly MaxOrderScan _bidOrderScan = new();
    private readonly MaxOrderScan _askOrderScan = new();
    private LevelStats _askStats; // The snapshot being rendered, see LevelStats.Scan
    private LevelStats _bidStats;

    public SkiaRenderer(RenderConfig config)
    {
        _config = config;

        // Initialize reusable paints (critical for 60 FPS zero-allocation)
        _bidBackgroundPaint = new SKPaint { Color = _config.BidBackground, Style = SKPaintStyle.Fill };
        _askBackgroundPaint = new SKPaint { Color = _config.AskBackground, Style = SKPaintStyle.Fill };
        _priceBackgroundPaint = new SKPaint { Color = _config.PriceBackground, Style = SKPaintStyle.Fill };
        _orderCountBackgroundPaint = new SKPaint { Color = _config.OrderCountBackground, Style = SKPaintStyle.Fill };
        _backgroundPaint = new SKPaint { Color = _config.BackgroundColor, Style = SKPaintStyle.Fill };

        // Text is drawn as cached blobs (TextBlobCache), so the paints carry only color and AA
        _textPaint = new SKPaint { Color = _config.TextColor, IsAntialias = true };

        _bidVolumePaint = new SKPaint { Color = _config.BidVolumeBar, Style = SKPaintStyle.Fill };
        _askVolumePaint = new SKPaint { Color = _config.AskVolumeBar, Style = SKPaintStyle.Fill };

        // Grid lines are 1px rects (DrawGridLine), the same pixels as a 1px non-AA stroke without stroking a path
        _gridLinePaint = new SKPaint
        {
            Color = _config.GridLineColor,
            Style = SKPaintStyle.Fill,
            IsAntialias = false
        };

        _highlightPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 30), // Semi-transparent white
            Style = SKPaintStyle.Fill
        };

        _dirtyRowPaint = new SKPaint
        {
            Color = _config.DirtyRowOverlayColor,
            Style = SKPaintStyle.Fill
        };

        _ownOrderBorderPaint = new SKPaint
        {
            Color = _config.OwnOrderBorderColor,
            StrokeWidth = 2,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };

        _segmentTextPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };

        var typeface = SKTypeface.FromFamilyName(_config.FontFamily);
        _textBlobs = new TextBlobCache(new SKFont(typeface, _config.FontSize));
        _segmentTextBlobs = new TextBlobCache(new SKFont(typeface, 10)); // Smaller font for segments
        _priceTexts = new TextCache<decimal>(price => price.ToString(_priceFormat));
        _quantityTexts = new TextCache<decimal>(FormatQuantityText);
    }

    /// <summary>
    /// Main render method - platform-agnostic, works on WPF and Avalonia
    /// Uses price-to-row mapping with viewport scroll offset support.
    /// Draws changed rows into an internal cache, then copies the whole frame onto canvas. On a
    /// GPU canvas (Avalonia) the cache is a texture on the same GPU, so the copy is a GPU draw and
    /// no frame is uploaded
    /// </summary>
    public void Render(SKCanvas canvas, OrderBookSnapshot snapshot, ViewportManager viewport)
    {
        EnsureCache(viewport.Width, viewport.Height, canvas.Context);
        var visibleRows = RenderRows(_cachedSurface!, snapshot, viewport);
        if (_onGpu)
        {
            canvas.DrawSurface(_cachedSurface, 0, 0);
        }
        else
        {
            if (_pixelsChanged)
            {
                // New pixels need a new image: a GPU canvas keys the texture it uploaded on the image
                // (its pixel ref), so writing to the same image's pixels would keep the first frame
                _cachedImage?.Dispose();
                _cachedSurface!.PeekPixels(_pixmap);
                _cachedImage = SKImage.FromPixels(_pixmap);
            }
            canvas.DrawImage(_cachedImage, 0, 0);
        }
        DrawDebugOverlay(canvas, viewport, visibleRows);
    }

    /// <summary>
    /// Render straight into a raster surface that keeps its pixels between calls and that
    /// nothing else draws to, such as WPF SKElement's back buffer: only changed rows are drawn
    /// and there is no cache copy. Rows are moved by writing its pixels directly, which Skia's
    /// copy-on-write doesn't see, so don't hold a Snapshot() of it across calls: those writes
    /// would land in the snapshot. New surface memory or size redraws everything. A transformed
    /// or GPU surface goes through Render. Call Invalidate after drawing into it yourself
    /// </summary>
    public void RenderRetained(SKSurface target, OrderBookSnapshot snapshot, ViewportManager viewport)
    {
        if (!target.PeekPixels(_pixmap) || !target.Canvas.TotalMatrix.IsIdentity)
        {
            Render(target.Canvas, snapshot, viewport);
            return;
        }

        var visibleRows = RenderRows(target, snapshot, viewport);
        if (_config.DebugDirtyRows)
        {
            DrawDebugOverlay(target.Canvas, viewport, visibleRows);
            _needsFullRedraw = true; // The overlay covers retained pixels: repaint them next frame
        }
    }

    /// <summary>
    /// Redraw everything on the next render
    /// </summary>
    public void Invalidate()
    {
        _needsFullRedraw = true;
    }

    private void DrawDebugOverlay(SKCanvas canvas, ViewportManager viewport, int visibleRows)
    {
        if (!_config.DebugDirtyRows)
        {
            return;
        }

        if (_debugOverlayAllRows)
        {
            DrawDirtyRowOverlayAll(canvas, viewport, visibleRows);
        }
        else
        {
            DrawDirtyRowOverlay(canvas, viewport, _debugDirtyRows, visibleRows);
        }
    }

    /// <summary>
    /// Bring surface up to date with the snapshot, redrawing only what changed since the last
    /// call on the same pixels. Returns the visible row count
    /// </summary>
    private int RenderRows(SKSurface surface, OrderBookSnapshot snapshot, ViewportManager viewport)
    {
        // Row states describe one block of pixels: anything else starts from scratch. A GPU
        // surface (only ever the cache) has no pixels to peek, so its handle stands in for them
        _onGpu = !surface.PeekPixels(_pixmap);
        var pixels = _onGpu
            ? (surface.Handle, _cachedWidth, _cachedHeight, 0)
            : (_pixmap.GetPixels(), _pixmap.Width, _pixmap.Height, _pixmap.RowBytes);
        if (pixels != _rowsPixels)
        {
            _rowsPixels = pixels;
            _needsFullRedraw = true;
        }
        _surface = surface;
        _pixelsChanged = false;
        var targetCanvas = surface.Canvas;

        _askStats = LevelStats.Scan(snapshot.Asks);
        _bidStats = LevelStats.Scan(snapshot.Bids);

        // Largest level sizes the quantity column and scales every volume bar
        var maxQuantity = Math.Max(_bidStats.MaxQuantity, _askStats.MaxQuantity);
        var newQtyColWidth = CalculateQuantityColumnWidth(maxQuantity);
        var newPriceColWidth = CalculatePriceColumnWidth();

        if (Math.Abs(viewport.QtyColWidth - newQtyColWidth) > 0.5f || Math.Abs(viewport.PriceColWidth - newPriceColWidth) > 0.5f)
        {
            viewport.QtyColWidth = newQtyColWidth;
            viewport.PriceColWidth = newPriceColWidth;
            _needsFullRedraw = true; // Force full redraw when column widths change
        }

        // Recalculate base scale if order quantities changed
        CalculateBaseScale(snapshot);

        var maxVolume = viewport.ShowVolumeBars ? maxQuantity : 0m;

        // Calculate visible rows - add 1 to ensure we fill to the bottom
        var visibleRows = (viewport.Height + RenderConfig.RowHeight - 1) / RenderConfig.RowHeight;
        var midRow = visibleRows / 2;
        _currentDisplayTickSize = viewport.DisplayTickSize;
        var displayTickSize = _currentDisplayTickSize;
        if (viewport.AutoCenter)
        {
            viewport.Recenter(snapshot);
        }
        var referencePrice = viewport.CenterPrice != 0
            ? viewport.CenterPrice
            : (snapshot.MidPrice ?? 50000m);

        if (_config.DebugDirtyRows)
        {
            _debugDirtyRows.Clear();
            _debugOverlayAllRows = false;
        }

        // Compare the rows this frame should show with what the cache holds and redraw only the
        // ones that differ. A layout change (or a move that isn't whole rows) redraws everything
        var layout = new LayoutState(viewport, _config, _segmentState);
        BuildRows(snapshot, viewport, maxVolume, visibleRows, midRow, displayTickSize, referencePrice);
        if (_needsFullRedraw || layout != _lastLayout || _rows.Length != _nextRows.Length ||
            !TryShiftRows(viewport, referencePrice, displayTickSize))
        {
            RenderFull(targetCanvas, viewport, maxVolume);
        }
        else
        {
            RenderChangedRows(targetCanvas, viewport, maxVolume);
        }
        (_rows, _nextRows) = (_nextRows, _rows);

        UpdateLastState(snapshot, viewport, referencePrice, layout);
        _surface = null; // A host's surface may not outlive its paint call
        return visibleRows;
    }

    /// <summary>
    /// False when Render would draw nothing new: same snapshot and view as the last call. Hosts
    /// use it to skip invalidating idle ladders, which also saves the blit to the target
    /// </summary>
    public bool NeedsRender(in OrderBookSnapshot snapshot, ViewportManager viewport)
    {
        return _needsFullRedraw ||
            !ReferenceEquals(snapshot.Bids, _lastSnapshot.Bids) ||
            !ReferenceEquals(snapshot.Asks, _lastSnapshot.Asks) ||
            !ReferenceEquals(snapshot.BidOrders, _lastSnapshot.BidOrders) ||
            !ReferenceEquals(snapshot.AskOrders, _lastSnapshot.AskOrders) ||
            snapshot.Timestamp != _lastSnapshot.Timestamp ||
            snapshot.MidPrice != _lastSnapshot.MidPrice ||
            snapshot.BestBid != _lastSnapshot.BestBid ||
            snapshot.BestAsk != _lastSnapshot.BestAsk ||
            new ViewState(new LayoutState(viewport, _config, _segmentState), viewport) != _lastView;
    }

    private void DrawColumnBackgrounds(SKCanvas canvas, ViewportManager viewport)
    {
        // Column order: Bid Order Count | Bid Qty | Price | Ask Qty | Ask Order Count | Volume Bars

        // Bid order count column (darker background to match web - NOT the same as bid qty)
        if (viewport.ShowOrderCount)
        {
            canvas.DrawRect(
                viewport.BidOrderCountColumnX, 0,
                RenderConfig.ColumnWidth, viewport.Height,
                _orderCountBackgroundPaint);
        }

        // Bid quantity column (blue background) - uses dynamic width
        canvas.DrawRect(
            viewport.BidQtyColumnX, 0,
            viewport.QtyColWidth, viewport.Height,
            _bidBackgroundPaint);

        // Price column (gray background) - uses dynamic width
        canvas.DrawRect(
            viewport.PriceColumnX, 0,
            viewport.PriceColWidth, viewport.Height,
            _priceBackgroundPaint);

        // Ask quantity column (red background) - uses dynamic width
        canvas.DrawRect(
            viewport.AskQtyColumnX, 0,
            viewport.QtyColWidth, viewport.Height,
            _askBackgroundPaint);

        // Ask order count column (darker background to match web - NOT the same as ask qty)
        if (viewport.ShowOrderCount)
        {
            canvas.DrawRect(
                viewport.AskOrderCountColumnX, 0,
                RenderConfig.ColumnWidth, viewport.Height,
                _orderCountBackgroundPaint);
        }
    }

    private void DrawRowBackgrounds(SKCanvas canvas, ViewportManager viewport, float y)
    {
        // The columns tile [0, columnsRight) edge to edge (same float sums), so the background
        // only needs to fill what's right of them
        var columnsRight = viewport.ShowOrderCount
            ? viewport.AskOrderCountColumnX + RenderConfig.ColumnWidth
            : viewport.AskQtyColumnX + viewport.QtyColWidth;
        canvas.DrawRect(columnsRight, y, Math.Max(0, viewport.Width - columnsRight), RenderConfig.RowHeight, _backgroundPaint);

        if (viewport.ShowOrderCount)
        {
            canvas.DrawRect(
                viewport.BidOrderCountColumnX, y,
                RenderConfig.ColumnWidth, RenderConfig.RowHeight,
                _orderCountBackgroundPaint);
        }

        canvas.DrawRect(
            viewport.BidQtyColumnX, y,
            viewport.QtyColWidth, RenderConfig.RowHeight,
            _bidBackgroundPaint);

        canvas.DrawRect(
            viewport.PriceColumnX, y,
            viewport.PriceColWidth, RenderConfig.RowHeight,
            _priceBackgroundPaint);

        canvas.DrawRect(
            viewport.AskQtyColumnX, y,
            viewport.QtyColWidth, RenderConfig.RowHeight,
            _askBackgroundPaint);

        if (viewport.ShowOrderCount)
        {
            canvas.DrawRect(
                viewport.AskOrderCountColumnX, y,
                RenderConfig.ColumnWidth, RenderConfig.RowHeight,
                _orderCountBackgroundPaint);
        }
    }

    private void DrawGridLine(SKCanvas canvas, ViewportManager viewport, float y)
    {
        canvas.DrawRect(0, y, viewport.Width, 1, _gridLinePaint);
    }

    /// <summary>
    /// What a render needs from all of one side's levels, found in one pass (mirrors TypeScript
    /// LevelStats). Everything after it reads only the levels it draws
    /// </summary>
    private readonly record struct LevelStats(
        decimal MaxQuantity,
        decimal MaxPrice, // 0 when no level is above 0
        decimal MinPrice, // decimal.MaxValue for no levels
        int NonEmpty, // Levels with quantity
        bool NonEmptyAscending, // Those strictly ascending by price
        bool Ascending) // All levels ascending by price, so the visible ones can be found by binary search
    {
        public static LevelStats Scan(BookLevel[] levels)
        {
            decimal maxQuantity = 0;
            decimal maxPrice = 0;
            var minPrice = decimal.MaxValue;
            var nonEmpty = 0;
            var nonEmptyAscending = true;
            var ascending = true;
            var lastPrice = decimal.MinValue;
            decimal lastNonEmptyPrice = 0;
            foreach (ref readonly var level in levels.AsSpan())
            {
                var price = level.Price;
                if (price > maxPrice) maxPrice = price;
                if (price < minPrice) minPrice = price;
                if (price < lastPrice) ascending = false;
                lastPrice = price;
                if (level.Quantity > maxQuantity) maxQuantity = level.Quantity;
                if (level.Quantity > 0)
                {
                    if (nonEmpty > 0 && price <= lastNonEmptyPrice) nonEmptyAscending = false;
                    lastNonEmptyPrice = price;
                    nonEmpty++;
                }
            }
            return new LevelStats(maxQuantity, maxPrice, minPrice, nonEmpty, nonEmptyAscending, ascending);
        }
    }

    // First index whose price is at or above price, in levels ascending by price
    private static int FirstAtOrAbove(BookLevel[] levels, decimal price)
    {
        int low = 0, high = levels.Length;
        while (low < high)
        {
            var mid = (low + high) >>> 1;
            if (levels[mid].Price < price)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }
        return low;
    }

    /// <summary>
    /// Calculate dynamic quantity column width based on maximum quantity in snapshot
    /// Mirrors TypeScript calculateQuantityColumnWidth() in canvas-renderer.ts
    /// </summary>
    private float CalculateQuantityColumnWidth(decimal maxQty)
    {
        if (maxQty == 0)
        {
            return RenderConfig.ColumnWidth;
        }

        // Format quantity (trim trailing zeros for decimals, use N0 for integers)
        var maxQtyText = maxQty % 1 == 0
            ? maxQty.ToString("N0")  // Integer: use thousands separators
            : maxQty.ToString("0.################").TrimEnd('0').TrimEnd('.');  // Decimal: trim trailing zeros

        // Measure text width
        var textWidth = _textBlobs.Get(maxQtyText).Width;

        // Add padding (10px each side)
        var requiredWidth = textWidth + 20;

        // Return max of standard column width or required width
        return Math.Max(RenderConfig.ColumnWidth, (float)Math.Ceiling(requiredWidth));
    }

    /// <summary>
    /// Calculate dynamic price column width based on price values
    /// Mirrors TypeScript calculatePriceColumnWidth() in canvas-renderer.ts
    /// </summary>
    private float CalculatePriceColumnWidth()
    {
        // The maximum and minimum prices determine max digits
        var maxPrice = Math.Max(_bidStats.MaxPrice, _askStats.MaxPrice);
        var minPrice = Math.Min(_bidStats.MinPrice, _askStats.MinPrice);

        if (maxPrice == 0 || minPrice == decimal.MaxValue)
        {
            return RenderConfig.ColumnWidth;
        }

        // Format the maximum price to get its text width
        var maxPriceText = FormatPrice(maxPrice);

        // Measure text width
        var textWidth = _textBlobs.Get(maxPriceText).Width;

        // Add padding (10px each side)
        var requiredWidth = textWidth + 20;

        // Return max of standard column width or required width
        return Math.Max(RenderConfig.ColumnWidth, (float)Math.Ceiling(requiredWidth));
    }

    /// <summary>
    /// A cache of the target's size, on the target's GPU (gpu) or in memory (null)
    /// </summary>
    private void EnsureCache(int width, int height, GRRecordingContext? gpu)
    {
        if (gpu != null)
        {
            FreeGpuCaches(gpu);
        }
        var context = gpu?.Handle ?? IntPtr.Zero;
        if (_cachedSurface != null && _cachedWidth == width && _cachedHeight == height && _cachedFor == context)
        {
            return;
        }

        _cachedImage?.Dispose();
        _cachedImage = null; // Made by Render after the full redraw below
        _cachedSurface?.Dispose();
        // Opaque (every pixel is painted) so blitting it to the target is a copy, not a blend
        var info = new SKImageInfo(width, height, SKImageInfo.PlatformColorType, SKAlphaType.Opaque);
        _cachedSurface = gpu == null ? null : SKSurface.Create(gpu, false, info) ??
            SKSurface.Create(gpu, false, info.WithColorType(SKColorType.Rgba8888));
        if (_cachedSurface != null)
        {
            // Freed only on this thread (Dispose or EnsureCache), never by the finalizer thread
            GC.SuppressFinalize(_cachedSurface);
        }
        _cachedSurface ??= SKSurface.Create(info); // No GPU, or no texture there: memory
        _cachedFor = context;
        _cachedThread = Environment.CurrentManagedThreadId;
        _cachedWidth = width;
        _cachedHeight = height;
        _needsFullRedraw = true;
    }

    /// <summary>
    /// Free GPU caches that disposed renderers left for this GPU's render thread
    /// </summary>
    private static void FreeGpuCaches(GRRecordingContext gpu)
    {
        for (var count = s_gpuCachesToFree.Count; count > 0 && s_gpuCachesToFree.TryDequeue(out var cache); count--)
        {
            if (cache.Context == gpu.Handle)
            {
                cache.Surface.Dispose();
            }
            else
            {
                s_gpuCachesToFree.Enqueue(cache); // Another GPU's
            }
        }
    }

    private void UpdateLastState(in OrderBookSnapshot snapshot, ViewportManager viewport, decimal referencePrice, LayoutState layout)
    {
        _needsFullRedraw = false;
        _lastLayout = layout;
        _lastReferencePrice = referencePrice;
        _lastSnapshot = snapshot;
        _lastView = new ViewState(layout, viewport);
        _lastBarMaxWidth = viewport.VolumeBarMaxWidth;
    }

    /// <summary>
    /// Fill _nextRows with what each visible row should show this frame
    /// </summary>
    private void BuildRows(
        OrderBookSnapshot snapshot,
        ViewportManager viewport,
        decimal maxVolume,
        int visibleRows,
        int midRow,
        decimal displayTickSize,
        decimal referencePrice)
    {
        if (_nextRows.Length != visibleRows)
        {
            _nextRows = new RowState[visibleRows];
        }
        var rows = _nextRows;

        if (viewport.RemovalMode == LevelRemovalMode.RemoveRow)
        {
            // Dense packing: asks then bids on consecutive rows, empty rows elsewhere
            Array.Fill(rows, new RowState { Drawn = true });
            var layout = BuildDensePackingLayout(snapshot, viewport, visibleRows, midRow);

            for (int i = 0; i < layout.AskRowsToRender; i++)
            {
                var askIndex = layout.NonEmptyAsks.Length - 1 - layout.StartAskIndex - i;
                var row = layout.FirstRowIndex + i;
                if (askIndex >= 0 && askIndex < layout.NonEmptyAsks.Length && row < rows.Length)
                {
                    rows[row].Ask = CreateRowLevel(layout.NonEmptyAsks[askIndex], snapshot.AskOrders, viewport, maxVolume);
                }
            }

            var firstBidRow = layout.FirstRowIndex + layout.AskRowsToRender;
            for (int i = 0; i < layout.BidRowsToRender; i++)
            {
                var bidIndex = layout.NonEmptyBids.Length - 1 - layout.StartBidIndex - i;
                var row = firstBidRow + i;
                if (bidIndex >= 0 && bidIndex < layout.NonEmptyBids.Length && row < rows.Length)
                {
                    rows[row].Bid = CreateRowLevel(layout.NonEmptyBids[bidIndex], snapshot.BidOrders, viewport, maxVolume);
                }
            }
            return;
        }

        // Show Empty Rows: every row is a price step from the reference price
        for (int rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var rowOffset = rowIndex - midRow;
            var price = referencePrice - (rowOffset * displayTickSize);
            rows[rowIndex] = new RowState { Drawn = true, Label = Math.Round(price / displayTickSize) * displayTickSize };
        }

        PlaceLevels(rows, snapshot.Asks, _askStats, snapshot.AskOrders, isAsk: true, viewport, maxVolume, midRow, displayTickSize, referencePrice);
        PlaceLevels(rows, snapshot.Bids, _bidStats, snapshot.BidOrders, isAsk: false, viewport, maxVolume, midRow, displayTickSize, referencePrice);
    }

    /// <summary>
    /// Show Empty Rows: put each level on the row its price maps to (mirrors TypeScript placeLevels).
    /// Ascending levels are read only around the rows' prices
    /// </summary>
    private void PlaceLevels(
        RowState[] rows,
        BookLevel[] levels,
        in LevelStats stats,
        Dictionary<decimal, Order[]>? ordersByPrice,
        bool isAsk,
        ViewportManager viewport,
        decimal maxVolume,
        int midRow,
        decimal displayTickSize,
        decimal referencePrice)
    {
        var start = 0;
        var maxPrice = decimal.MaxValue;
        if (stats.Ascending)
        {
            // Rows span midRow - rows.Length + 1 .. midRow ticks from the reference price. A tick
            // more on each side covers rounding, and the row check below has the final say
            start = FirstAtOrAbove(levels, referencePrice + (midRow - rows.Length - 0.5m) * displayTickSize);
            maxPrice = referencePrice + (midRow + 1.5m) * displayTickSize;
        }
        for (var i = start; i < levels.Length; i++)
        {
            ref readonly var level = ref levels[i];
            if (level.Price > maxPrice)
            {
                break;
            }
            var priceDelta = level.Price - referencePrice;
            var rowIndex = midRow - (int)Math.Round(priceDelta / displayTickSize);
            if (rowIndex < 0 || rowIndex >= rows.Length)
            {
                continue;
            }

            ref var row = ref rows[rowIndex];
            ref var slot = ref isAsk ? ref row.Ask : ref row.Bid;
            var rowLevel = CreateRowLevel(level, ordersByPrice, viewport, maxVolume);
            if (!slot.Present)
            {
                slot = rowLevel;
            }
            else
            {
                // Prices off the display grid can share a row: draw them all, in snapshot order
                (row.Overlaps ??= new List<(RowLevel, bool)>()).Add((rowLevel, isAsk));
            }
        }
    }

    private RowLevel CreateRowLevel(in BookLevel level, Dictionary<decimal, Order[]>? ordersByPrice, ViewportManager viewport, decimal maxVolume)
    {
        Order[]? orders = null;
        ordersByPrice?.TryGetValue(level.Price, out orders);
        // Same condition as the aggregated bar in Draw*Level*
        var bar = viewport.ShowVolumeBars && viewport.VolumeBarColumnX.HasValue && maxVolume > 0 &&
                  level.Quantity != 0 && (orders == null || orders.Length == 0)
            ? CalculateVolumeBarWidth(level.Quantity, maxVolume, viewport.VolumeBarMaxWidth)
            : 0f;
        return new RowLevel(level, orders, bar);
    }

    /// <summary>
    /// Show Empty Rows: when the reference price moves by whole rows, move the cached pixels and
    /// row states with it, so only rows scrolling in (or changed) get redrawn. False when the
    /// move can't be reused (off the display grid, or a screen or more)
    /// </summary>
    private bool TryShiftRows(ViewportManager viewport, decimal referencePrice, decimal displayTickSize)
    {
        if (viewport.RemovalMode != LevelRemovalMode.ShowEmpty || referencePrice == _lastReferencePrice)
        {
            return true;
        }

        var steps = (referencePrice - _lastReferencePrice) / displayTickSize; // Price up: content moves down
        if (steps != decimal.Truncate(steps) || Math.Abs(steps) >= _rows.Length)
        {
            return false;
        }
        var shift = (int)steps;

        if (_onGpu)
        {
            // Skia copies the texture before a draw writes to it while a snapshot of it is held
            using var frame = _surface!.Snapshot();
            _surface.Canvas.DrawImage(frame, 0, shift * RenderConfig.RowHeight);
        }
        else
        {
            var pixels = _pixmap.GetPixelSpan();
            var shiftBytes = Math.Abs(shift) * RenderConfig.RowHeight * _pixmap.RowBytes;
            if (shift > 0)
            {
                pixels[..^shiftBytes].CopyTo(pixels[shiftBytes..]); // Overlap safe (memmove)
            }
            else
            {
                pixels[shiftBytes..].CopyTo(pixels);
            }
        }
        _pixelsChanged = true;

        // A row keeps its state only if it came from a strip that was fully on screen
        var fullRows = _rowsPixels.Height / RenderConfig.RowHeight;
        if (shift > 0)
        {
            for (int rowIndex = _rows.Length - 1; rowIndex >= 0; rowIndex--)
            {
                var from = rowIndex - shift;
                _rows[rowIndex] = from >= 0 && from < fullRows ? _rows[from] : default;
            }
        }
        else
        {
            for (int rowIndex = 0; rowIndex < _rows.Length; rowIndex++)
            {
                var from = rowIndex - shift;
                _rows[rowIndex] = from < _rows.Length && from < fullRows ? _rows[from] : default;
            }
        }
        return true;
    }

    private void RenderFull(SKCanvas canvas, ViewportManager viewport, decimal maxVolume)
    {
        _pixelsChanged = true;
        canvas.Clear(_config.BackgroundColor);
        DrawColumnBackgrounds(canvas, viewport);
        DrawGridLines(canvas, viewport);

        for (int rowIndex = 0; rowIndex < _nextRows.Length; rowIndex++)
        {
            DrawRowContent(canvas, viewport, in _nextRows[rowIndex], maxVolume, rowIndex * RenderConfig.RowHeight);
        }

        if (_config.DebugDirtyRows)
        {
            _debugOverlayAllRows = true;
        }
    }

    /// <summary>
    /// Redraw the rows whose state differs from the cache. Each row strip (background, its top
    /// grid line, content) comes out the same as RenderFull draws it
    /// </summary>
    private void RenderChangedRows(SKCanvas canvas, ViewportManager viewport, decimal maxVolume)
    {
        Span<int> source = stackalloc int[_nextRows.Length];
        if (FindMovedRows(source))
        {
            MoveRows(source);
        }

        if (!_onGpu)
        {
            EnsureRowTemplate(viewport, _pixmap.Info);
        }
        // First pixel column a non-AA bar rect can touch; the columns end just before it. A GPU
        // fills pixels whose centers a rect covers, so a half-pixel edge starts a pixel earlier
        var barColumn = !viewport.VolumeBarColumnX.HasValue ? -1
            : (int)MathF.Floor(viewport.VolumeBarColumnX.Value + (_onGpu ? 0 : 0.5f));

        for (int rowIndex = 0; rowIndex < _nextRows.Length; rowIndex++)
        {
            ref readonly var cached = ref _rows[rowIndex];
            ref readonly var next = ref _nextRows[rowIndex];
            if (cached.SameAs(in next))
            {
                continue;
            }

            var y = rowIndex * RenderConfig.RowHeight;
            if (barColumn < 0 || !cached.SameAs(in next, compareBars: false))
            {
                // Blank strip, then content. The blank strip carries only this row's top grid
                // line: the line below belongs to the next row, and redrawing it would cut into
                // that row's own-order border
                ClearRow(canvas, viewport, rowIndex, 0);
                DrawRowContent(canvas, viewport, in next, maxVolume, y);
            }
            else if (barColumn >= _rowsPixels.Width)
            {
                // Only the bars changed, and a narrow ladder puts the whole bar column past the right edge
                continue;
            }
            else if (!_onGpu && (viewport.RemovalMode == LevelRemovalMode.ShowEmpty || viewport.ShowOrderCount ||
                     !next.Ask.Level.HasOwnOrders))
            {
                // Only the bars changed (the volume scale moved): reset the bar column, draw the bars
                ClearRow(canvas, viewport, rowIndex, barColumn);
                DrawBars(canvas, viewport, in next, y);
            }
            else
            {
                // Replay the row clipped to the bar column: an ask own-order border reaches 1px into
                // it, and on a GPU so can the columns' last pixel
                canvas.Save();
                canvas.ClipRect(new SKRect(barColumn, y, viewport.Width, y + RenderConfig.RowHeight));
                ClearRow(canvas, viewport, rowIndex, barColumn);
                DrawRowContent(canvas, viewport, in next, maxVolume, y);
                canvas.Restore();
            }

            _pixelsChanged = true;
            if (_config.DebugDirtyRows)
            {
                _debugDirtyRows.Add(rowIndex);
            }
        }
    }

    /// <summary>
    /// Aggregated bars only, as DrawRowContent draws them (asks before bids)
    /// </summary>
    private void DrawBars(SKCanvas canvas, ViewportManager viewport, in RowState row, float y)
    {
        var barX = viewport.VolumeBarColumnX!.Value;
        if (row.Ask.Bar > 0)
        {
            canvas.DrawRect(barX, y + 4, row.Ask.Bar, RenderConfig.RowHeight - 8, _askVolumePaint);
        }
        if (row.Bid.Bar > 0)
        {
            canvas.DrawRect(barX, y + 4, row.Bid.Bar, RenderConfig.RowHeight - 8, _bidVolumePaint);
        }
    }

    /// <summary>
    /// Pixels of a blank row strip (DrawRowBackgrounds + its top grid line) for the current layout,
    /// in the target's pixel format. Copying it is ~3x cheaper than drawing those rects
    /// </summary>
    private void EnsureRowTemplate(ViewportManager viewport, SKImageInfo targetInfo)
    {
        var info = new SKImageInfo(targetInfo.Width, RenderConfig.RowHeight, targetInfo.ColorType, targetInfo.AlphaType);
        if (_rowTemplateLayout == _lastLayout && _rowTemplateInfo == info)
        {
            return;
        }

        using var surface = SKSurface.Create(info);
        DrawRowBackgrounds(surface.Canvas, viewport, 0);
        DrawGridLine(surface.Canvas, viewport, 0);
        using var pixmap = surface.PeekPixels();
        _rowTemplate = pixmap.GetPixelSpan().ToArray();
        _rowTemplateRowBytes = pixmap.RowBytes;
        _rowTemplateLayout = _lastLayout;
        _rowTemplateInfo = info;
    }

    /// <summary>
    /// Blank a row strip (backgrounds and its top grid line) from pixel column fromX to the right
    /// edge. Raster copies the template. A GPU draws the rects, as RenderFull does there: the
    /// template's edge pixels can differ from how the GPU fills a fractional column edge
    /// </summary>
    private void ClearRow(SKCanvas canvas, ViewportManager viewport, int rowIndex, int fromX)
    {
        if (_onGpu)
        {
            // Callers clip to fromX
            var y = rowIndex * RenderConfig.RowHeight;
            DrawRowBackgrounds(canvas, viewport, y);
            DrawGridLine(canvas, viewport, y);
        }
        else
        {
            CopyRowTemplate(_pixmap.GetPixelSpan(), _pixmap.RowBytes, _pixmap.Width, _pixmap.Height, rowIndex, fromX);
        }
    }

    /// <summary>
    /// Copy the blank row template into a row strip, from pixel column fromX to the right edge
    /// </summary>
    private void CopyRowTemplate(Span<byte> pixels, int rowBytes, int width, int height, int rowIndex, int fromX)
    {
        var bytesPerPixel = _rowTemplateInfo.BytesPerPixel;
        var length = (width - fromX) * bytesPerPixel;
        var top = rowIndex * RenderConfig.RowHeight;
        var lines = Math.Min(RenderConfig.RowHeight, height - top);
        if (fromX == 0 && rowBytes == _rowTemplateRowBytes)
        {
            // Whole lines laid out alike: the strip is one block
            _rowTemplate.AsSpan(0, lines * rowBytes).CopyTo(pixels.Slice(top * rowBytes, lines * rowBytes));
            return;
        }
        for (int line = 0; line < lines; line++)
        {
            _rowTemplate.AsSpan(line * _rowTemplateRowBytes + fromX * bytesPerPixel, length)
                .CopyTo(pixels.Slice((top + line) * rowBytes + fromX * bytesPerPixel, length));
        }
    }

    /// <summary>
    /// For each changed row, a nearby cached row that already shows its content (-1: none).
    /// Levels added or removed above a row shift it in Remove Rows mode
    /// </summary>
    private bool FindMovedRows(Span<int> source)
    {
        var fullRows = _rowsPixels.Height / RenderConfig.RowHeight; // A row cut off at the bottom can't be a source
        var anyMoved = false;
        for (int rowIndex = 0; rowIndex < source.Length; rowIndex++)
        {
            source[rowIndex] = -1;
            ref readonly var next = ref _nextRows[rowIndex];
            if (_rows[rowIndex].SameAs(in next, compareBars: false))
            {
                continue;
            }

            for (int distance = 1; distance <= MaxRowMove; distance++)
            {
                var above = rowIndex - distance;
                if (above >= 0 && above < fullRows && _rows[above].SameAs(in next, compareBars: false))
                {
                    source[rowIndex] = above;
                    break;
                }
                var below = rowIndex + distance;
                if (below < fullRows && below < _rows.Length && _rows[below].SameAs(in next, compareBars: false))
                {
                    source[rowIndex] = below;
                    break;
                }
            }
            anyMoved |= source[rowIndex] >= 0;
        }
        return anyMoved;
    }

    /// <summary>
    /// Copy row strips to their new rows: up moves top-down, down moves bottom-up, so a strip is
    /// read before it's overwritten. A move whose source was already overwritten (moves in both
    /// directions crossing) is dropped and its row redrawn instead
    /// </summary>
    private void MoveRows(ReadOnlySpan<int> source)
    {
        _pixelsChanged = true;
        if (_onGpu)
        {
            MoveRowsOnGpu(source);
            return;
        }

        var pixels = _pixmap.GetPixelSpan();
        var stripBytes = RenderConfig.RowHeight * _pixmap.RowBytes;
        Span<bool> overwritten = stackalloc bool[source.Length];

        for (int rowIndex = 0; rowIndex < source.Length; rowIndex++)
        {
            if (source[rowIndex] > rowIndex)
            {
                MoveRow(pixels, stripBytes, overwritten, source[rowIndex], rowIndex);
            }
        }
        for (int rowIndex = source.Length - 1; rowIndex >= 0; rowIndex--)
        {
            if (source[rowIndex] >= 0 && source[rowIndex] < rowIndex)
            {
                MoveRow(pixels, stripBytes, overwritten, source[rowIndex], rowIndex);
            }
        }
    }

    private void MoveRow(Span<byte> pixels, int stripBytes, Span<bool> overwritten, int from, int to)
    {
        if (overwritten[from])
        {
            return;
        }
        var start = to * stripBytes;
        var length = Math.Min(stripBytes, pixels.Length - start); // Destination may be cut off at the bottom
        pixels.Slice(from * stripBytes, length).CopyTo(pixels.Slice(start, length));
        _rows[to] = _rows[from];
        overwritten[to] = true;
    }

    /// <summary>
    /// GPU surface: draw row strips from a snapshot of the last frame, so no source is overwritten
    /// before it's read. Neighbors moving by the same amount go in one draw
    /// </summary>
    private void MoveRowsOnGpu(ReadOnlySpan<int> source)
    {
        if (_rowsBeforeMove.Length != _rows.Length)
        {
            _rowsBeforeMove = new RowState[_rows.Length];
        }
        _rows.CopyTo(_rowsBeforeMove, 0);
        using var frame = _surface!.Snapshot();
        var width = _rowsPixels.Width;

        for (int rowIndex = 0; rowIndex < source.Length;)
        {
            var from = source[rowIndex];
            if (from < 0)
            {
                rowIndex++;
                continue;
            }

            var end = rowIndex + 1;
            while (end < source.Length && source[end] >= 0 && source[end] - end == from - rowIndex)
            {
                end++;
            }
            var y = rowIndex * RenderConfig.RowHeight;
            var height = Math.Min((end - rowIndex) * RenderConfig.RowHeight, _rowsPixels.Height - y); // May be cut off at the bottom
            _surface.Canvas.DrawImage(frame, SKRect.Create(0, from * RenderConfig.RowHeight, width, height),
                SKRect.Create(0, y, width, height));

            for (; rowIndex < end; rowIndex++)
            {
                _rows[rowIndex] = _rowsBeforeMove[source[rowIndex]];
            }
        }
    }

    private void DrawRowContent(SKCanvas canvas, ViewportManager viewport, in RowState row, decimal maxVolume, float y)
    {
        if (viewport.RemovalMode == LevelRemovalMode.RemoveRow)
        {
            if (row.Ask.Present)
            {
                DrawAskLevel(canvas, row.Ask.Level, viewport, maxVolume, y, row.Ask.Orders);
            }
            else if (row.Bid.Present)
            {
                DrawBidLevel(canvas, row.Bid.Level, viewport, maxVolume, y, row.Bid.Orders);
            }
            return;
        }

        var textY = TextY(y);
        DrawCellText(
            canvas,
            FormatPrice(row.Label),
            viewport.PriceColumnX + (viewport.PriceColWidth / 2),
            textY);

        // Asks before bids, each in snapshot order
        if (row.Ask.Present)
        {
            DrawAskLevelQuantity(canvas, row.Ask.Level, viewport, maxVolume, y, row.Ask.Orders);
        }
        DrawOverlaps(canvas, viewport, in row, isAsk: true, maxVolume, y);
        if (row.Bid.Present)
        {
            DrawBidLevelQuantity(canvas, row.Bid.Level, viewport, maxVolume, y, row.Bid.Orders);
        }
        DrawOverlaps(canvas, viewport, in row, isAsk: false, maxVolume, y);
    }

    private void DrawOverlaps(SKCanvas canvas, ViewportManager viewport, in RowState row, bool isAsk, decimal maxVolume, float y)
    {
        if (row.Overlaps == null)
        {
            return;
        }

        foreach (var (level, levelIsAsk) in row.Overlaps)
        {
            if (levelIsAsk != isAsk)
            {
                continue;
            }
            if (isAsk)
            {
                DrawAskLevelQuantity(canvas, level.Level, viewport, maxVolume, y, level.Orders);
            }
            else
            {
                DrawBidLevelQuantity(canvas, level.Level, viewport, maxVolume, y, level.Orders);
            }
        }
    }

    /// <summary>
    /// Everything a row's pixels depend on besides its RowState. A change redraws every row
    /// </summary>
    private readonly record struct LayoutState(
        int Width,
        int Height,
        LevelRemovalMode RemovalMode,
        bool ShowOrderCount,
        bool ShowVolumeBars,
        decimal TickSize,
        decimal DisplayTickSize,
        int DenseScrollOffset,
        float QtyColWidth,
        float PriceColWidth,
        decimal MinQuantityThreshold,
        long MboOrderSizeFilter,
        double SegmentPixelsPerUnit,
        double SegmentScrollOffset)
    {
        public LayoutState(ViewportManager viewport, RenderConfig config, SegmentRenderState segments) : this(
            viewport.Width,
            viewport.Height,
            viewport.RemovalMode,
            viewport.ShowOrderCount,
            viewport.ShowVolumeBars,
            viewport.TickSize,
            viewport.DisplayTickSize,
            viewport.RemovalMode == LevelRemovalMode.RemoveRow ? viewport.DensePackingScrollOffset : 0,
            viewport.QtyColWidth,
            viewport.PriceColWidth,
            config.MinQuantityThreshold,
            config.MboOrderSizeFilter,
            segments.BasePixelsPerUnit * segments.UserScaleFactor,
            segments.HorizontalScrollOffset)
        {
        }
    }

    /// <summary>
    /// The viewport as Render last left it (it recenters and sets column widths)
    /// </summary>
    private readonly record struct ViewState(LayoutState Layout, bool AutoCenter, decimal CenterPrice, int DensePackingScrollOffset)
    {
        public ViewState(LayoutState layout, ViewportManager viewport)
            : this(layout, viewport.AutoCenter, viewport.CenterPrice, viewport.DensePackingScrollOffset)
        {
        }
    }

    /// <summary>
    /// What one cached row shows. Under the same LayoutState, rows that are SameAs each other
    /// have identical pixels, so only rows that differ from the new frame are redrawn
    /// </summary>
    private struct RowState
    {
        public bool Drawn;     // False: pixels unknown (not drawn yet, or shifted in)
        public decimal Label;  // Show Empty Rows price label
        public RowLevel Ask;
        public RowLevel Bid;
        public List<(RowLevel Level, bool IsAsk)>? Overlaps; // More levels on this row, always redrawn

        public readonly bool SameAs(in RowState other, bool compareBars = true)
        {
            return Drawn && other.Drawn && Overlaps == null && other.Overlaps == null && Label == other.Label &&
                   Ask.SameAs(in other.Ask, compareBars) && Bid.SameAs(in other.Bid, compareBars);
        }
    }

    private readonly struct RowLevel
    {
        public readonly bool Present;
        public readonly BookLevel Level;
        public readonly Order[]? Orders; // MBOManager replaces a level's array when it changes, never mutates it
        public readonly float Bar;       // Aggregated bar width: a volume scale change redraws the row

        public RowLevel(BookLevel level, Order[]? orders, float bar)
        {
            Present = true;
            Level = level;
            Orders = orders;
            Bar = bar;
        }

        public bool SameAs(in RowLevel other, bool compareBars)
        {
            if (Present != other.Present) return false;
            if (!Present) return true;
            return Level.Price == other.Level.Price &&
                   Level.Quantity == other.Level.Quantity &&
                   Level.NumOrders == other.Level.NumOrders &&
                   Level.HasOwnOrders == other.Level.HasOwnOrders &&
                   ReferenceEquals(Orders, other.Orders) &&
                   (!compareBars || Bar == other.Bar);
        }
    }

    private void DrawDirtyRowOverlayAll(SKCanvas canvas, ViewportManager viewport, int visibleRows)
    {
        for (int rowIndex = 0; rowIndex <= visibleRows; rowIndex++)
        {
            var y = rowIndex * RenderConfig.RowHeight;
            if (y < 0 || y >= viewport.Height)
            {
                continue;
            }

            canvas.DrawRect(0, y, viewport.Width, RenderConfig.RowHeight, _dirtyRowPaint);
        }
    }

    private void DrawDirtyRowOverlay(
        SKCanvas canvas,
        ViewportManager viewport,
        HashSet<int> dirtyRows,
        int visibleRows)
    {
        foreach (var rowIndex in dirtyRows)
        {
            if (rowIndex < 0 || rowIndex > visibleRows)
            {
                continue;
            }

            var y = rowIndex * RenderConfig.RowHeight;
            if (y < 0 || y >= viewport.Height)
            {
                continue;
            }

            canvas.DrawRect(0, y, viewport.Width, RenderConfig.RowHeight, _dirtyRowPaint);
        }
    }

    private readonly struct DensePackingLayout
    {
        public readonly BookLevel[] NonEmptyAsks;
        public readonly BookLevel[] NonEmptyBids;
        public readonly int StartAskIndex;
        public readonly int AskRowsToRender;
        public readonly int StartBidIndex;
        public readonly int BidRowsToRender;
        public readonly float TopOffset;
        public readonly int FirstRowIndex;

        public DensePackingLayout(
            BookLevel[] nonEmptyAsks,
            BookLevel[] nonEmptyBids,
            int startAskIndex,
            int askRowsToRender,
            int startBidIndex,
            int bidRowsToRender,
            float topOffset,
            int firstRowIndex)
        {
            NonEmptyAsks = nonEmptyAsks;
            NonEmptyBids = nonEmptyBids;
            StartAskIndex = startAskIndex;
            AskRowsToRender = askRowsToRender;
            StartBidIndex = startBidIndex;
            BidRowsToRender = bidRowsToRender;
            TopOffset = topOffset;
            FirstRowIndex = firstRowIndex;
        }
    }

    private DensePackingLayout BuildDensePackingLayout(
        OrderBookSnapshot snapshot,
        ViewportManager viewport,
        int visibleRows,
        int midRow)
    {
        var nonEmptyAsks = NonEmptyLevels(snapshot.Asks, _askStats);
        var nonEmptyBids = NonEmptyLevels(snapshot.Bids, _bidStats);
        var scrollOffset = viewport.DensePackingScrollOffset;
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
            startAskIndex = nonEmptyAsks.Length;
            askRowsToRender = 0;
            startBidIndex = nonEmptyBids.Length;
            bidRowsToRender = 0;
        }

        var firstRowIndex = (int)(topOffset / RenderConfig.RowHeight);

        return new DensePackingLayout(
            nonEmptyAsks,
            nonEmptyBids,
            startAskIndex,
            askRowsToRender,
            startBidIndex,
            bidRowsToRender,
            topOffset,
            firstRowIndex);
    }

    /// <summary>
    /// Levels with quantity, ascending by price, one per price (the last one wins). Snapshots
    /// arrive sorted and unique, so the dedupe + sort only runs for input that isn't
    /// </summary>
    private static BookLevel[] NonEmptyLevels(BookLevel[] levels, in LevelStats stats)
    {
        if (!stats.NonEmptyAscending)
        {
            var byPrice = new Dictionary<decimal, BookLevel>();
            foreach (var level in levels)
            {
                if (level.Quantity > 0) byPrice[level.Price] = level;
            }
            return byPrice.Values.OrderBy(l => l.Price).ToArray();
        }

        if (stats.NonEmpty == levels.Length)
        {
            return levels; // Read-only below, so the snapshot's array is reused
        }

        var nonEmpty = new BookLevel[stats.NonEmpty];
        var count = 0;
        foreach (var level in levels)
        {
            if (level.Quantity > 0) nonEmpty[count++] = level;
        }
        return nonEmpty;
    }

    private void DrawGridLines(SKCanvas canvas, ViewportManager viewport)
    {
        var visibleRows = viewport.Height / RenderConfig.RowHeight;

        for (int i = 0; i <= visibleRows; i++)
        {
            DrawGridLine(canvas, viewport, i * RenderConfig.RowHeight);
        }
    }

    /// <summary>
    /// Draw individual order bars for MBO mode with proportional sizing (mirrors TypeScript drawIndividualOrders)
    /// </summary>
    private void DrawIndividualOrders(
        SKCanvas canvas,
        Order[] orders,
        Side side,
        ViewportManager viewport,
        decimal maxVolume,
        float y)
    {
        if (orders == null || orders.Length == 0 || !viewport.ShowVolumeBars || !viewport.VolumeBarColumnX.HasValue)
        {
            return;
        }

        // Calculate segment widths without minimum constraint
        var pixelsPerUnit = _segmentState.BasePixelsPerUnit * _segmentState.UserScaleFactor;
        var scrollOffset = _segmentState.HorizontalScrollOffset;
        var barStartX = viewport.VolumeBarColumnX.Value;
        var barColumnWidth = viewport.VolumeBarMaxWidth;
        var barHeight = RenderConfig.RowHeight - 8;
        var minQty = _config.MboOrderSizeFilter;
        var paint = side == Side.BID ? _bidVolumePaint : _askVolumePaint;

        // Clip to bar column so scrolled segments/text don't bleed into qty columns.
        var clipRect = new SKRect(barStartX, y + 4, barStartX + barColumnWidth, y + 4 + barHeight);
        canvas.Save();
        canvas.ClipRect(clipRect);

        double xOffset = 0;  // Track position within virtual segment space
        var gap = RenderConfig.SegmentGapPx;

        for (int i = 0; i < orders.Length; i++)
        {
            var order = orders[i];

            if (order.Quantity <= minQty)
            {
                continue;
            }

            // Calculate proportional width (no min constraint)
            var segmentWidth = (double)(double)order.Quantity * pixelsPerUnit;

            // Apply minimum rendering width
            var renderWidth = (float)Math.Max(RenderConfig.MinSegmentWidthPx, segmentWidth);

            // Calculate screen position accounting for scroll
            var segmentStartX = (float)(barStartX + xOffset - scrollOffset);
            var segmentEndX = segmentStartX + renderWidth;

            // Cull segments outside visible area
            var visibleStartX = Math.Max(segmentStartX, barStartX);
            var visibleEndX = Math.Min(segmentEndX, barStartX + barColumnWidth);

            if (visibleEndX > visibleStartX && visibleEndX > barStartX && visibleStartX < barStartX + barColumnWidth)
            {
                var visibleWidth = visibleEndX - visibleStartX;

                // Draw segment background
                canvas.DrawRect(visibleStartX, y + 4, visibleWidth, barHeight, paint);

                // Draw exact quantity text (no K/M formatting)
                var qtyText = FormatQuantity(order.Quantity);
                var textWidth = _segmentTextBlobs.Get(qtyText).Width;

                // Only draw text if segment is wide enough and text is in visible area
                if (renderWidth > 40 && textWidth < renderWidth - 4)
                {
                    var textCenterX = segmentStartX + renderWidth / 2;

                    // Check if text center is in visible area
                    if (textCenterX >= barStartX && textCenterX <= barStartX + barColumnWidth)
                    {
                        var textY = y + 4 + barHeight / 2 + 3;  // Vertically centered
                        DrawCenteredText(canvas, _segmentTextBlobs, qtyText, textCenterX, textY, _segmentTextPaint);
                    }
                }

                // Draw gold border for own orders
                if (order.IsOwnOrder)
                {
                    canvas.DrawRect(visibleStartX, y + 4, visibleWidth, barHeight, _ownOrderBorderPaint);
                }
            }

            // Move to next segment position
            xOffset += renderWidth + gap;
        }

        canvas.Restore();
    }

    /// <summary>
    /// Format quantity for display (smart formatting: integers with commas, decimals with trimmed zeros)
    /// </summary>
    private string FormatQuantity(decimal quantity)
    {
        if (_quantityTextThreshold != _config.MinQuantityThreshold)
        {
            _quantityTexts.Clear();
            _quantityTextThreshold = _config.MinQuantityThreshold;
        }
        return _quantityTexts.Get(quantity);
    }

    private string FormatQuantityText(decimal quantity)
    {
        // If quantity is below threshold, show "<threshold" instead
        if (quantity > 0 && quantity < _config.MinQuantityThreshold)
        {
            var decimalPlaces = GetDecimalPlaces(_config.MinQuantityThreshold);
            if (decimalPlaces == 0)
            {
                return $"<{_config.MinQuantityThreshold}";
            }
            return $"<{_config.MinQuantityThreshold.ToString($"F{decimalPlaces}")}";
        }

        if (quantity % 1 == 0)
        {
            // Integer: use thousands separator (e.g., "1,234")
            return quantity.ToString("N0");
        }
        else
        {
            // Decimal: show decimal places, trim trailing zeros (e.g., "123.45")
            return quantity.ToString("0.################").TrimEnd('0').TrimEnd('.');
        }
    }

    private string FormatPrice(decimal price)
    {
        if (_priceFormatTickSize != _currentDisplayTickSize)
        {
            _priceFormat = $"F{GetDecimalPlaces(_currentDisplayTickSize)}";
            _priceFormatTickSize = _currentDisplayTickSize;
            _priceTexts.Clear();
        }
        return _priceTexts.Get(price);
    }

    /// <summary>
    /// Formatted text by value, so redrawing a cell doesn't build its string again. Cleared when
    /// full, which bounds memory without per-entry upkeep
    /// </summary>
    private sealed class TextCache<T> where T : notnull
    {
        private const int Capacity = 4096;
        private readonly Dictionary<T, string> _texts = new();
        private readonly Func<T, string> _format;

        public TextCache(Func<T, string> format) => _format = format;

        public string Get(T value)
        {
            if (!_texts.TryGetValue(value, out var text))
            {
                if (_texts.Count >= Capacity)
                {
                    _texts.Clear();
                }
                text = _format(value);
                _texts[value] = text;
            }
            return text;
        }

        public void Clear() => _texts.Clear();
    }

    // Baseline for cell text in the row starting at y
    private float TextY(float y) => y + (RenderConfig.RowHeight / 2) + (_config.FontSize / 3);

    private void DrawCellText(SKCanvas canvas, string text, float centerX, float y)
    {
        DrawCenteredText(canvas, _textBlobs, text, centerX, y, _textPaint);
    }

    /// <summary>
    /// Same pixels as canvas.DrawText(text, centerX, y, paint) with TextAlign.Center, which
    /// measures the text and builds a blob on every call
    /// </summary>
    private static void DrawCenteredText(SKCanvas canvas, TextBlobCache cache, string text, float centerX, float y, SKPaint paint)
    {
        var run = cache.Get(text);
        if (run.Blob != null)
        {
            canvas.DrawText(run.Blob, centerX - run.Width * 0.5f, y, paint);
        }
    }

    private readonly record struct TextRun(SKTextBlob? Blob, float Width);

    /// <summary>
    /// Text blobs keyed by string, with their advance width. Ladder text repeats frame to frame,
    /// so a hit skips glyph lookup, measuring and blob allocation. Two generations: a blob not
    /// used during a whole generation is freed, bounding memory without per-entry LRU upkeep
    /// </summary>
    private sealed class TextBlobCache : IDisposable
    {
        private const int GenerationSize = 512;
        private readonly SKFont _font;
        private Dictionary<string, TextRun> _current = new();
        private Dictionary<string, TextRun> _previous = new();

        // Takes ownership of font
        public TextBlobCache(SKFont font) => _font = font;

        public TextRun Get(string text)
        {
            if (_current.TryGetValue(text, out var run)) return run;
            if (!_previous.Remove(text, out run))
            {
                run = new TextRun(SKTextBlob.Create(text, _font), _font.MeasureText(text));
            }
            if (_current.Count >= GenerationSize)
            {
                Free(_previous);
                (_current, _previous) = (_previous, _current);
            }
            _current[text] = run;
            return run;
        }

        private static void Free(Dictionary<string, TextRun> runs)
        {
            foreach (var run in runs.Values) run.Blob?.Dispose();
            runs.Clear();
        }

        public void Dispose()
        {
            Free(_current);
            Free(_previous);
            _font.Dispose();
        }
    }

    private int GetDecimalPlaces(decimal threshold)
    {
        if (threshold >= 1.0m) return 0;
        var str = threshold.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var dotIndex = str.IndexOf('.');
        if (dotIndex == -1) return 0;

        var fractionalPart = str.Substring(dotIndex + 1).TrimEnd('0');
        return fractionalPart.Length;
    }

    private void DrawBidLevel(SKCanvas canvas, BookLevel level, ViewportManager viewport, decimal maxVolume, float y, Order[]? orders = null)
    {
        // Calculate text baseline (centered vertically in row)
        var textY = TextY(y);

        // Column order: Bid Order Count | Bid Qty | Price | Ask Qty | Ask Order Count | Volume Bars
        // BID: Draw bid order count, bid qty, and price only (leave ask columns empty)

        var isEmpty = level.Quantity == 0;

        // Bid order count (if enabled and not empty)
        if (viewport.ShowOrderCount && !isEmpty)
        {
            DrawCellText(
                canvas,
                _orderCountTexts.Get(level.NumOrders),
                viewport.BidOrderCountColumnX + (RenderConfig.ColumnWidth / 2),
                textY);
        }

        // Bid quantity (skip if empty) - uses dynamic width
        if (!isEmpty)
        {
            DrawCellText(
                canvas,
                FormatQuantity(level.Quantity),
                viewport.BidQtyColumnX + (viewport.QtyColWidth / 2),
                textY);
        }

        // Own order highlight in PriceLevel mode
        if (level.HasOwnOrders)
        {
            canvas.DrawRect(viewport.BidQtyColumnX, y + 1, viewport.QtyColWidth, RenderConfig.RowHeight - 2, _ownOrderBorderPaint);
        }

        // Price (always show, even if empty)
        DrawCellText(
            canvas,
            FormatPrice(level.Price),
            viewport.PriceColumnX + (viewport.PriceColWidth / 2),
            textY);

        // Draw volume bars
        if (viewport.ShowVolumeBars && viewport.VolumeBarColumnX.HasValue && maxVolume > 0 && !isEmpty)
        {
            if (orders != null && orders.Length > 0)
            {
                // MBO mode: Draw individual order bars
                DrawIndividualOrders(canvas, orders, Side.BID, viewport, maxVolume, y);
            }
            else
            {
                // PriceLevel mode: Draw single aggregated bar
                var barWidth = CalculateVolumeBarWidth(level.Quantity, maxVolume, viewport.VolumeBarMaxWidth);

                canvas.DrawRect(
                    viewport.VolumeBarColumnX.Value,
                    y + 4,
                    barWidth,
                    RenderConfig.RowHeight - 8,
                    _bidVolumePaint);
            }
        }
    }

    private void DrawAskLevel(SKCanvas canvas, BookLevel level, ViewportManager viewport, decimal maxVolume, float y, Order[]? orders = null)
    {
        // Calculate text baseline (centered vertically in row)
        var textY = TextY(y);

        // Column order: Bid Order Count | Bid Qty | Price | Ask Qty | Ask Order Count | Volume Bars
        // ASK: Draw price, ask qty, and ask order count only (leave bid columns empty)

        var isEmpty = level.Quantity == 0;

        // Price (always show, even if empty)
        DrawCellText(
            canvas,
            FormatPrice(level.Price),
            viewport.PriceColumnX + (viewport.PriceColWidth / 2),
            textY);

        // Ask quantity (skip if empty) - uses dynamic width
        if (!isEmpty)
        {
            DrawCellText(
                canvas,
                FormatQuantity(level.Quantity),
                viewport.AskQtyColumnX + (viewport.QtyColWidth / 2),
                textY);
        }

        // Own order highlight in PriceLevel mode
        if (level.HasOwnOrders)
        {
            canvas.DrawRect(viewport.AskQtyColumnX, y + 1, viewport.QtyColWidth, RenderConfig.RowHeight - 2, _ownOrderBorderPaint);
        }

        // Ask order count (if enabled and not empty)
        if (viewport.ShowOrderCount && !isEmpty)
        {
            DrawCellText(
                canvas,
                _orderCountTexts.Get(level.NumOrders),
                viewport.AskOrderCountColumnX + (RenderConfig.ColumnWidth / 2),
                textY);
        }

        // Draw volume bars
        if (viewport.ShowVolumeBars && viewport.VolumeBarColumnX.HasValue && maxVolume > 0 && !isEmpty)
        {
            if (orders != null && orders.Length > 0)
            {
                // MBO mode: Draw individual order bars
                DrawIndividualOrders(canvas, orders, Side.ASK, viewport, maxVolume, y);
            }
            else
            {
                // PriceLevel mode: Draw single aggregated bar
                var barWidth = CalculateVolumeBarWidth(level.Quantity, maxVolume, viewport.VolumeBarMaxWidth);

                canvas.DrawRect(
                    viewport.VolumeBarColumnX.Value,
                    y + 4,
                    barWidth,
                    RenderConfig.RowHeight - 8,
                    _askVolumePaint);
            }
        }
    }

    private void DrawBidLevelQuantity(SKCanvas canvas, BookLevel level, ViewportManager viewport, decimal maxVolume, float y, Order[]? orders = null)
    {
        // Draw only quantity data (price already drawn in Step 1)
        var textY = TextY(y);
        var isEmpty = level.Quantity == 0;

        // Bid order count (if enabled and not empty)
        if (viewport.ShowOrderCount && !isEmpty)
        {
            DrawCellText(
                canvas,
                _orderCountTexts.Get(level.NumOrders),
                viewport.BidOrderCountColumnX + (RenderConfig.ColumnWidth / 2),
                textY);
        }

        // Bid quantity (skip if empty) - uses dynamic width
        if (!isEmpty)
        {
            DrawCellText(
                canvas,
                FormatQuantity(level.Quantity),
                viewport.BidQtyColumnX + (viewport.QtyColWidth / 2),
                textY);
        }

        // Draw volume bars
        if (viewport.ShowVolumeBars && viewport.VolumeBarColumnX.HasValue && maxVolume > 0 && !isEmpty)
        {
            if (orders != null && orders.Length > 0)
            {
                // MBO mode: Draw individual order bars
                DrawIndividualOrders(canvas, orders, Side.BID, viewport, maxVolume, y);
            }
            else
            {
                // PriceLevel mode: Draw single aggregated bar
                var barWidth = CalculateVolumeBarWidth(level.Quantity, maxVolume, viewport.VolumeBarMaxWidth);

                canvas.DrawRect(
                    viewport.VolumeBarColumnX.Value,
                    y + 4,
                    barWidth,
                    RenderConfig.RowHeight - 8,
                    _bidVolumePaint);
            }
        }
    }

    private void DrawAskLevelQuantity(SKCanvas canvas, BookLevel level, ViewportManager viewport, decimal maxVolume, float y, Order[]? orders = null)
    {
        // Draw only quantity data (price already drawn in Step 1)
        var textY = TextY(y);
        var isEmpty = level.Quantity == 0;

        // Ask quantity (skip if empty) - uses dynamic width
        if (!isEmpty)
        {
            DrawCellText(
                canvas,
                FormatQuantity(level.Quantity),
                viewport.AskQtyColumnX + (viewport.QtyColWidth / 2),
                textY);
        }

        // Ask order count (if enabled and not empty)
        if (viewport.ShowOrderCount && !isEmpty)
        {
            DrawCellText(
                canvas,
                _orderCountTexts.Get(level.NumOrders),
                viewport.AskOrderCountColumnX + (RenderConfig.ColumnWidth / 2),
                textY);
        }

        // Draw volume bars
        if (viewport.ShowVolumeBars && viewport.VolumeBarColumnX.HasValue && maxVolume > 0 && !isEmpty)
        {
            if (orders != null && orders.Length > 0)
            {
                // MBO mode: Draw individual order bars
                DrawIndividualOrders(canvas, orders, Side.ASK, viewport, maxVolume, y);
            }
            else
            {
                // PriceLevel mode: Draw single aggregated bar
                var barWidth = CalculateVolumeBarWidth(level.Quantity, maxVolume, viewport.VolumeBarMaxWidth);

                canvas.DrawRect(
                    viewport.VolumeBarColumnX.Value,
                    y + 4,
                    barWidth,
                    RenderConfig.RowHeight - 8,
                    _askVolumePaint);
            }
        }
    }

    private void DrawHoverHighlight(SKCanvas canvas, decimal price, ViewportManager viewport)
    {
        var y = viewport.PriceToPixel(price);
        canvas.DrawRect(0, y, viewport.Width, RenderConfig.RowHeight, _highlightPaint);
    }

    private float CalculateVolumeBarWidth(decimal quantity, decimal maxVolume, float maxWidth)
    {
        if (maxVolume == 0) return 0;
        return (float)(quantity / maxVolume) * maxWidth;
    }

    /// <summary>
    /// Calculate base scale factor from max order quantity (mirrors TypeScript calculateBaseScale)
    /// Formula: basePixelsPerUnit = TARGET_MAX_SEGMENT_WIDTH / maxOrderQuantity
    /// </summary>
    private void CalculateBaseScale(OrderBookSnapshot snapshot)
    {
        var minQty = _config.MboOrderSizeFilter;
        var maxOrderQty = Math.Max(_bidOrderScan.Max(snapshot.BidOrders, minQty), _askOrderScan.Max(snapshot.AskOrders, minQty));

        if (maxOrderQty > 0)
        {
            var newBaseScale = (double)RenderConfig.TargetMaxSegmentWidth / (double)maxOrderQty;

            var changeRatio = Math.Abs(newBaseScale - _segmentState.BasePixelsPerUnit)
                             / _segmentState.BasePixelsPerUnit;

            // Only update if significantly different (20% threshold) or initial calculation
            if (changeRatio > 0.2 || _segmentState.BasePixelsPerUnit == 1.0)
            {
                _segmentState.BasePixelsPerUnit = newBaseScale;
            }
        }
    }

    /// <summary>
    /// Largest order above the size filter in one side's order map. MBOManager hands out a new map
    /// only when that side's orders change (and never changes one it handed out), so a quiet side
    /// isn't scanned again
    /// </summary>
    private sealed class MaxOrderScan
    {
        private Dictionary<decimal, Order[]>? _orders;
        private long _minQty;
        private decimal _max;

        public decimal Max(Dictionary<decimal, Order[]>? orders, long minQty)
        {
            if (ReferenceEquals(orders, _orders) && minQty == _minQty)
            {
                return _max;
            }

            _orders = orders;
            _minQty = minQty;
            _max = 0;
            if (orders != null)
            {
                foreach (var levelOrders in orders.Values)
                {
                    foreach (var order in levelOrders)
                    {
                        if (order.Quantity > minQty && order.Quantity > _max)
                        {
                            _max = order.Quantity;
                        }
                    }
                }
            }
            return _max;
        }
    }

    /// <summary>
    /// Max horizontal scroll offset from the widest level's segments (mirrors TypeScript
    /// recalculateMaxScroll). It walks every order of every level and only scrolling reads it, so
    /// it's found then
    /// </summary>
    private void RecalculateMaxScroll(in OrderBookSnapshot snapshot, float barMaxWidth)
    {
        if (snapshot.BidOrders == null && snapshot.AskOrders == null)
        {
            _segmentState.MaxScrollOffset = 0; // Price levels only: no segments to scroll
            return;
        }

        var pixelsPerUnit = _segmentState.BasePixelsPerUnit * _segmentState.UserScaleFactor;
        var maxWidth = Math.Max(WidestSegments(snapshot.Bids, snapshot.BidOrders, pixelsPerUnit),
            WidestSegments(snapshot.Asks, snapshot.AskOrders, pixelsPerUnit));
        _segmentState.MaxScrollOffset = Math.Max(0, maxWidth - barMaxWidth);
    }

    private double WidestSegments(BookLevel[] levels, Dictionary<decimal, Order[]>? ordersByPrice, double pixelsPerUnit)
    {
        double maxWidth = 0;
        if (ordersByPrice == null)
        {
            return maxWidth;
        }

        var minQty = _config.MboOrderSizeFilter;
        foreach (var level in levels)
        {
            if (!ordersByPrice.TryGetValue(level.Price, out var orders))
            {
                continue;
            }
            double totalWidth = 0;
            foreach (var order in orders)
            {
                if (order.Quantity > minQty)
                {
                    totalWidth += ((double)order.Quantity * pixelsPerUnit) + RenderConfig.SegmentGapPx;
                }
            }
            maxWidth = Math.Max(maxWidth, totalWidth);
        }
        return maxWidth;
    }

    private double CalculateDynamicSegmentScaleStep()
    {
        var baseStep = RenderConfig.SegmentScaleStep;
        var basePixelsPerUnit = _segmentState.BasePixelsPerUnit;

        if (basePixelsPerUnit <= 0)
        {
            return baseStep;
        }

        var maxOrderQtyEstimate = RenderConfig.TargetMaxSegmentWidth / basePixelsPerUnit;
        var qtyRatio = Math.Max(1.0, maxOrderQtyEstimate / 100.0);
        var qtyBoost = Math.Min(4.0, 1.0 + Math.Log10(qtyRatio));
        var zoomBoost = Math.Min(5.0, 1.0 + Math.Log(Math.Max(1.0, _segmentState.UserScaleFactor), 2.0));

        var step = baseStep * qtyBoost * zoomBoost;
        return Math.Max(baseStep, Math.Min(3.0, step));
    }

    /// <summary>
    /// Adjust segment scale factor (mirrors TypeScript adjustSegmentScale)
    /// </summary>
    public void AdjustSegmentScale(int delta, OrderBookSnapshot snapshot, ViewportManager viewport)
    {
        var step = CalculateDynamicSegmentScaleStep();
        var newScale = _segmentState.UserScaleFactor + (delta * step);

        // Clamp to valid range
        _segmentState.UserScaleFactor = Math.Max(
            RenderConfig.SegmentScaleMin,
            Math.Min(RenderConfig.SegmentScaleMax, newScale)
        );

        // Recalculate max scroll offset with new scale
        RecalculateMaxScroll(snapshot, viewport.VolumeBarMaxWidth);

        // Clamp current scroll to new max
        if (_segmentState.HorizontalScrollOffset > _segmentState.MaxScrollOffset)
        {
            _segmentState.HorizontalScrollOffset = _segmentState.MaxScrollOffset;
        }

        // Force full redraw
        _needsFullRedraw = true;
    }

    /// <summary>
    /// Adjust horizontal scroll offset (mirrors TypeScript adjustHorizontalScroll)
    /// </summary>
    public void AdjustHorizontalScroll(double delta)
    {
        RecalculateMaxScroll(_lastSnapshot, _lastBarMaxWidth); // The widest level changes with the book
        _segmentState.HorizontalScrollOffset = Math.Max(
            0,
            Math.Min(
                _segmentState.MaxScrollOffset,
                _segmentState.HorizontalScrollOffset + delta
            )
        );

        // Force full redraw
        _needsFullRedraw = true;
    }


    public void SetMboOrderSizeFilter(long filter)
    {
        var normalized = Math.Max(0, filter);
        if (_config.MboOrderSizeFilter == normalized)
        {
            return;
        }

        _config.MboOrderSizeFilter = normalized;
        _needsFullRedraw = true;
    }

    public void Dispose()
    {
        _bidBackgroundPaint?.Dispose();
        _askBackgroundPaint?.Dispose();
        _priceBackgroundPaint?.Dispose();
        _orderCountBackgroundPaint?.Dispose();
        _backgroundPaint?.Dispose();
        _textPaint?.Dispose();
        _bidVolumePaint?.Dispose();
        _askVolumePaint?.Dispose();
        _gridLinePaint?.Dispose();
        _highlightPaint?.Dispose();
        _dirtyRowPaint?.Dispose();
        _ownOrderBorderPaint?.Dispose();
        _segmentTextPaint?.Dispose();
        _textBlobs.Dispose();
        _segmentTextBlobs.Dispose();
        _cachedImage?.Dispose();
        if (Environment.CurrentManagedThreadId == _cachedThread || !DeferGpuCache())
        {
            _cachedSurface?.Dispose();
        }
        _pixmap.Dispose();
        GC.SuppressFinalize(this);
    }

    // Dropped without Dispose. The GPU cache's own finalizer is suppressed (it would free the
    // texture on this thread), so it's left for the next render on its GPU. The rest have
    // finalizers of their own
    ~SkiaRenderer()
    {
        DeferGpuCache();
    }

    // A GPU cache goes to the render thread of its GPU to be freed
    private bool DeferGpuCache()
    {
        if (_cachedFor == IntPtr.Zero || _cachedSurface == null)
        {
            return false;
        }
        s_gpuCachesToFree.Enqueue((_cachedFor, _cachedSurface));
        _cachedSurface = null;
        return true;
    }
}
