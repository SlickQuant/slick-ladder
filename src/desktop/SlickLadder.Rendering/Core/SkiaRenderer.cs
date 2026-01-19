using SkiaSharp;
using SlickLadder.Core;
using SlickLadder.Core.Managers;
using SlickLadder.Core.Models;
using System;
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
    private readonly HashSet<int> _debugDirtyRows = new HashSet<int>();
    private bool _debugOverlayAllRows;

    private readonly RenderConfig _config;
    private SKSurface? _cachedSurface;
    private int _cachedWidth;
    private int _cachedHeight;
    private bool _needsFullRedraw = true;
    private LevelRemovalMode _lastRemovalMode;
    private bool _lastShowOrderCount;
    private bool _lastShowVolumeBars;
    private decimal _lastTickSize;
    private decimal _lastReferencePrice;
    private int _lastDensePackingScrollOffset;
    private decimal _lastCenterPrice;

    // Segment rendering state (mirrors TypeScript SegmentRenderState)
    private SegmentRenderState _segmentState = new SegmentRenderState();

    public SkiaRenderer(RenderConfig config)
    {
        _config = config;

        // Initialize reusable paints (critical for 60 FPS zero-allocation)
        _bidBackgroundPaint = new SKPaint { Color = _config.BidBackground, Style = SKPaintStyle.Fill };
        _askBackgroundPaint = new SKPaint { Color = _config.AskBackground, Style = SKPaintStyle.Fill };
        _priceBackgroundPaint = new SKPaint { Color = _config.PriceBackground, Style = SKPaintStyle.Fill };
        _orderCountBackgroundPaint = new SKPaint { Color = _config.OrderCountBackground, Style = SKPaintStyle.Fill };
        _backgroundPaint = new SKPaint { Color = _config.BackgroundColor, Style = SKPaintStyle.Fill };

        _textPaint = new SKPaint
        {
            Color = _config.TextColor,
            TextSize = _config.FontSize,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName(_config.FontFamily),
            TextAlign = SKTextAlign.Center
        };

        _bidVolumePaint = new SKPaint { Color = _config.BidVolumeBar, Style = SKPaintStyle.Fill };
        _askVolumePaint = new SKPaint { Color = _config.AskVolumeBar, Style = SKPaintStyle.Fill };

        _gridLinePaint = new SKPaint
        {
            Color = _config.GridLineColor,
            StrokeWidth = 1,
            Style = SKPaintStyle.Stroke,
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

        _segmentTextPaint = new SKPaint
        {
            Color = SKColors.White,
            TextSize = 10,  // Smaller font for segments
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName(_config.FontFamily),
            TextAlign = SKTextAlign.Center
        };
    }

    /// <summary>
    /// Main render method - platform-agnostic, works on WPF and Avalonia
    /// Uses price-to-row mapping with viewport scroll offset support
    /// </summary>
    public void Render(SKCanvas canvas, OrderBookSnapshot snapshot, ViewportManager viewport)
    {
        EnsureCache(viewport.Width, viewport.Height);
        var targetCanvas = _cachedSurface!.Canvas;

        // Recalculate base scale if order quantities changed
        CalculateBaseScale(snapshot);

        // Recalculate max scroll offset with current scale
        RecalculateMaxScroll(snapshot, viewport);

        // Calculate max volume for proportional bars
        var maxVolume = 0L;
        if (viewport.ShowVolumeBars)
        {
            var maxBid = snapshot.Bids.Length > 0 ? snapshot.Bids.Max(b => b.Quantity) : 0L;
            var maxAsk = snapshot.Asks.Length > 0 ? snapshot.Asks.Max(a => a.Quantity) : 0L;
            maxVolume = Math.Max(maxBid, maxAsk);
        }

        // Calculate visible rows - add 1 to ensure we fill to the bottom
        var visibleRows = (viewport.Height + RenderConfig.RowHeight - 1) / RenderConfig.RowHeight;
        var midRow = visibleRows / 2;
        var tickSize = viewport.TickSize;
        var referencePrice = viewport.CenterPrice != 0
            ? viewport.CenterPrice
            : (snapshot.MidPrice ?? 50000m);

        if (_config.DebugDirtyRows)
        {
            _debugDirtyRows.Clear();
            _debugOverlayAllRows = false;
        }

        var fullRedraw = ShouldFullRedraw(snapshot, viewport, referencePrice);
        if (fullRedraw)
        {
            RenderFull(targetCanvas, snapshot, viewport, maxVolume, visibleRows, midRow, tickSize, referencePrice);
        }
        else
        {
            RenderDirty(targetCanvas, snapshot, viewport, maxVolume, visibleRows, midRow, tickSize, referencePrice);
        }

        using var image = _cachedSurface!.Snapshot();
        canvas.DrawImage(image, 0, 0);

        if (_config.DebugDirtyRows)
        {
            if (_debugOverlayAllRows)
            {
                DrawDirtyRowOverlayAll(canvas, viewport, visibleRows);
            }
            else
            {
                DrawDirtyRowOverlay(canvas, viewport, _debugDirtyRows, visibleRows);
            }
        }

        UpdateLastState(snapshot, viewport, referencePrice);
    }

    private void DrawColumnBackgrounds(SKCanvas canvas, ViewportManager viewport)
    {
        // Column order: Bid Order Count | Bid Qty | Price | Ask Qty | Ask Order Count | Volume Bars

        // Bid order count column (darker background to match web - NOT the same as bid qty)
        if (viewport.ShowOrderCount)
        {
            canvas.DrawRect(
                viewport.BidOrderCountColumnX, 0,
                viewport.ColumnWidth, viewport.Height,
                _orderCountBackgroundPaint);
        }

        // Bid quantity column (blue background)
        canvas.DrawRect(
            viewport.BidQtyColumnX, 0,
            viewport.ColumnWidth, viewport.Height,
            _bidBackgroundPaint);

        // Price column (gray background)
        canvas.DrawRect(
            viewport.PriceColumnX, 0,
            viewport.ColumnWidth, viewport.Height,
            _priceBackgroundPaint);

        // Ask quantity column (red background)
        canvas.DrawRect(
            viewport.AskQtyColumnX, 0,
            viewport.ColumnWidth, viewport.Height,
            _askBackgroundPaint);

        // Ask order count column (darker background to match web - NOT the same as ask qty)
        if (viewport.ShowOrderCount)
        {
            canvas.DrawRect(
                viewport.AskOrderCountColumnX, 0,
                viewport.ColumnWidth, viewport.Height,
                _orderCountBackgroundPaint);
        }
    }

    private void DrawRowBackgrounds(SKCanvas canvas, ViewportManager viewport, float y)
    {
        canvas.DrawRect(0, y, viewport.Width, RenderConfig.RowHeight, _backgroundPaint);

        if (viewport.ShowOrderCount)
        {
            canvas.DrawRect(
                viewport.BidOrderCountColumnX, y,
                viewport.ColumnWidth, RenderConfig.RowHeight,
                _orderCountBackgroundPaint);
        }

        canvas.DrawRect(
            viewport.BidQtyColumnX, y,
            viewport.ColumnWidth, RenderConfig.RowHeight,
            _bidBackgroundPaint);

        canvas.DrawRect(
            viewport.PriceColumnX, y,
            viewport.ColumnWidth, RenderConfig.RowHeight,
            _priceBackgroundPaint);

        canvas.DrawRect(
            viewport.AskQtyColumnX, y,
            viewport.ColumnWidth, RenderConfig.RowHeight,
            _askBackgroundPaint);

        if (viewport.ShowOrderCount)
        {
            canvas.DrawRect(
                viewport.AskOrderCountColumnX, y,
                viewport.ColumnWidth, RenderConfig.RowHeight,
                _orderCountBackgroundPaint);
        }
    }

    private void DrawRowGridLines(SKCanvas canvas, ViewportManager viewport, float y)
    {
        var bottomY = y + RenderConfig.RowHeight;
        canvas.DrawLine(0, y, viewport.Width, y, _gridLinePaint);
        if (bottomY <= viewport.Height)
        {
            canvas.DrawLine(0, bottomY, viewport.Width, bottomY, _gridLinePaint);
        }
    }

    private void EnsureCache(int width, int height)
    {
        if (_cachedSurface != null && _cachedWidth == width && _cachedHeight == height)
        {
            return;
        }

        _cachedSurface?.Dispose();
        _cachedSurface = SKSurface.Create(new SKImageInfo(width, height));
        _cachedWidth = width;
        _cachedHeight = height;
        _needsFullRedraw = true;
    }

    private bool ShouldFullRedraw(OrderBookSnapshot snapshot, ViewportManager viewport, decimal referencePrice)
    {
        if (_needsFullRedraw || snapshot.DirtyChanges == null)
        {
            return true;
        }

        if (_lastRemovalMode != viewport.RemovalMode ||
            _lastShowOrderCount != viewport.ShowOrderCount ||
            _lastShowVolumeBars != viewport.ShowVolumeBars ||
            _lastTickSize != viewport.TickSize)
        {
            return true;
        }

        if (viewport.RemovalMode == LevelRemovalMode.RemoveRow)
        {
            if (_lastDensePackingScrollOffset != viewport.DensePackingScrollOffset)
            {
                return true;
            }
        }
        else
        {
            if (_lastReferencePrice != referencePrice || _lastCenterPrice != viewport.CenterPrice)
            {
                return true;
            }
        }

        return false;
    }

    private void UpdateLastState(OrderBookSnapshot snapshot, ViewportManager viewport, decimal referencePrice)
    {
        _needsFullRedraw = false;
        _lastRemovalMode = viewport.RemovalMode;
        _lastShowOrderCount = viewport.ShowOrderCount;
        _lastShowVolumeBars = viewport.ShowVolumeBars;
        _lastTickSize = viewport.TickSize;
        _lastReferencePrice = referencePrice;
        _lastDensePackingScrollOffset = viewport.DensePackingScrollOffset;
        _lastCenterPrice = viewport.CenterPrice;
    }

    private void RenderFull(
        SKCanvas canvas,
        OrderBookSnapshot snapshot,
        ViewportManager viewport,
        long maxVolume,
        int visibleRows,
        int midRow,
        decimal tickSize,
        decimal referencePrice)
    {
        canvas.Clear(_config.BackgroundColor);
        DrawColumnBackgrounds(canvas, viewport);
        DrawGridLines(canvas, viewport);

        if (viewport.RemovalMode == LevelRemovalMode.RemoveRow)
        {
            var layout = BuildDensePackingLayout(snapshot, viewport, visibleRows, midRow);

            float currentY = layout.TopOffset;
            for (int i = 0; i < layout.AskRowsToRender; i++)
            {
                var askIndex = layout.NonEmptyAsks.Length - 1 - layout.StartAskIndex - i;
                if (askIndex >= 0 && askIndex < layout.NonEmptyAsks.Length)
                {
                    var y = currentY + (i * RenderConfig.RowHeight);
                    if (y >= 0 && y < viewport.Height)
                    {
                        var level = layout.NonEmptyAsks[askIndex];
                        Order[]? orders = null;
                        snapshot.AskOrders?.TryGetValue(level.Price, out orders);
                        DrawAskLevel(canvas, level, viewport, maxVolume, y, orders);
                    }
                }
            }

            currentY = layout.TopOffset + (layout.AskRowsToRender * RenderConfig.RowHeight);
            for (int i = 0; i < layout.BidRowsToRender; i++)
            {
                var bidIndex = layout.NonEmptyBids.Length - 1 - layout.StartBidIndex - i;
                if (bidIndex >= 0 && bidIndex < layout.NonEmptyBids.Length)
                {
                    var y = currentY + (i * RenderConfig.RowHeight);
                    if (y >= 0 && y < viewport.Height)
                    {
                        var level = layout.NonEmptyBids[bidIndex];
                        Order[]? orders = null;
                        snapshot.BidOrders?.TryGetValue(level.Price, out orders);
                        DrawBidLevel(canvas, level, viewport, maxVolume, y, orders);
                    }
                }
            }
        }
        else
        {
            for (int rowIndex = 0; rowIndex <= visibleRows; rowIndex++)
            {
                var rowOffset = rowIndex - midRow;
                var price = referencePrice - (rowOffset * tickSize);
                price = Math.Round(price / tickSize) * tickSize;

                var y = rowIndex * RenderConfig.RowHeight;
                var textY = y + (RenderConfig.RowHeight / 2) + (_textPaint.TextSize / 3);

                canvas.DrawText(
                    price.ToString("F2"),
                    viewport.PriceColumnX + (viewport.ColumnWidth / 2),
                    textY,
                    _textPaint);
            }

            foreach (var level in snapshot.Asks)
            {
                var priceDelta = level.Price - referencePrice;
                var rowOffset = -(int)Math.Round(priceDelta / tickSize);
                var rowIndex = midRow + rowOffset;

                if (rowIndex >= 0 && rowIndex <= visibleRows)
                {
                    var y = rowIndex * RenderConfig.RowHeight;
                    Order[]? orders = null;
                    snapshot.AskOrders?.TryGetValue(level.Price, out orders);
                    DrawAskLevelQuantity(canvas, level, viewport, maxVolume, y, orders);
                }
            }

            foreach (var level in snapshot.Bids)
            {
                var priceDelta = level.Price - referencePrice;
                var rowOffset = -(int)Math.Round(priceDelta / tickSize);
                var rowIndex = midRow + rowOffset;

                if (rowIndex >= 0 && rowIndex <= visibleRows)
                {
                    var y = rowIndex * RenderConfig.RowHeight;
                    Order[]? orders = null;
                    snapshot.BidOrders?.TryGetValue(level.Price, out orders);
                    DrawBidLevelQuantity(canvas, level, viewport, maxVolume, y, orders);
                }
            }
        }
        if (_config.DebugDirtyRows)
        {
            _debugOverlayAllRows = true;
        }
    }

    private void RenderDirty(
        SKCanvas canvas,
        OrderBookSnapshot snapshot,
        ViewportManager viewport,
        long maxVolume,
        int visibleRows,
        int midRow,
        decimal tickSize,
        decimal referencePrice)
    {
        if (snapshot.DirtyChanges == null || snapshot.DirtyChanges.Length == 0)
        {
            return;
        }

        HashSet<int> dirtyRows;
        if (_config.DebugDirtyRows)
        {
            dirtyRows = _debugDirtyRows;
            dirtyRows.Clear();
        }
        else
        {
            dirtyRows = new HashSet<int>();
        }

        DensePackingLayout? denseLayout = null;
        if (viewport.RemovalMode == LevelRemovalMode.RemoveRow)
        {
            denseLayout = BuildDensePackingLayout(snapshot, viewport, visibleRows, midRow);
        }

        foreach (var change in snapshot.DirtyChanges)
        {
            int? rowIndex = null;
            if (viewport.RemovalMode == LevelRemovalMode.RemoveRow)
            {
                if (denseLayout.HasValue)
                {
                    rowIndex = GetDenseRowIndexForChange(change, denseLayout.Value);
                }
            }
            else
            {
                rowIndex = PriceToRowIndex(change.Price, referencePrice, tickSize, midRow);
            }

            if (rowIndex.HasValue)
            {
                dirtyRows.Add(rowIndex.Value);
            }
        }

        if (viewport.RemovalMode == LevelRemovalMode.RemoveRow && snapshot.StructuralChange && denseLayout.HasValue)
        {
            var minRow = visibleRows;
            var hasRow = false;

            foreach (var change in snapshot.DirtyChanges)
            {
                if (!change.IsStructural)
                {
                    continue;
                }

                var rowIndex = GetDenseRowIndexForChange(change, denseLayout.Value);
                if (rowIndex.HasValue)
                {
                    minRow = Math.Min(minRow, rowIndex.Value);
                    hasRow = true;
                }
            }

            if (!hasRow)
            {
                RenderFull(canvas, snapshot, viewport, maxVolume, visibleRows, midRow, tickSize, referencePrice);
                return;
            }

            for (int row = minRow; row <= visibleRows; row++)
            {
                dirtyRows.Add(row);
            }
        }

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

            DrawRowBackgrounds(canvas, viewport, y);
            DrawRowGridLines(canvas, viewport, y);

            if (viewport.RemovalMode == LevelRemovalMode.RemoveRow)
            {
                if (!denseLayout.HasValue)
                {
                    continue;
                }

                if (TryGetDenseLevelForRow(rowIndex, denseLayout.Value, out var level, out var side))
                {
                    if (side == Side.ASK)
                    {
                        Order[]? orders = null;
                        snapshot.AskOrders?.TryGetValue(level.Price, out orders);
                        DrawAskLevel(canvas, level, viewport, maxVolume, y, orders);
                    }
                    else
                    {
                        Order[]? orders = null;
                        snapshot.BidOrders?.TryGetValue(level.Price, out orders);
                        DrawBidLevel(canvas, level, viewport, maxVolume, y, orders);
                    }
                }
            }
            else
            {
                RenderShowEmptyRow(canvas, snapshot, viewport, maxVolume, rowIndex, y, midRow, tickSize, referencePrice);
            }
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

    private void RenderShowEmptyRow(
        SKCanvas canvas,
        OrderBookSnapshot snapshot,
        ViewportManager viewport,
        long maxVolume,
        int rowIndex,
        float y,
        int midRow,
        decimal tickSize,
        decimal referencePrice)
    {
        var rowOffset = rowIndex - midRow;
        var price = referencePrice - (rowOffset * tickSize);
        price = Math.Round(price / tickSize) * tickSize;

        var textY = y + (RenderConfig.RowHeight / 2) + (_textPaint.TextSize / 3);
        canvas.DrawText(
            price.ToString("F2"),
            viewport.PriceColumnX + (viewport.ColumnWidth / 2),
            textY,
            _textPaint);

        if (TryFindLevel(snapshot.Asks, price, out var askLevel))
        {
            Order[]? orders = null;
            snapshot.AskOrders?.TryGetValue(price, out orders);
            DrawAskLevelQuantity(canvas, askLevel, viewport, maxVolume, y, orders);
            return;
        }

        if (TryFindLevel(snapshot.Bids, price, out var bidLevel))
        {
            Order[]? orders = null;
            snapshot.BidOrders?.TryGetValue(price, out orders);
            DrawBidLevelQuantity(canvas, bidLevel, viewport, maxVolume, y, orders);
        }
    }

    private static bool TryFindLevel(ReadOnlySpan<BookLevel> levels, decimal price, out BookLevel level)
    {
        for (int i = 0; i < levels.Length; i++)
        {
            if (levels[i].Price == price)
            {
                level = levels[i];
                return true;
            }
        }

        level = default;
        return false;
    }

    private static int PriceToRowIndex(decimal price, decimal referencePrice, decimal tickSize, int midRow)
    {
        var priceDelta = price - referencePrice;
        var rowOffset = -(int)Math.Round(priceDelta / tickSize);
        return midRow + rowOffset;
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
        var nonEmptyAsks = snapshot.Asks.Where(l => l.Quantity > 0).ToArray();
        var nonEmptyBids = snapshot.Bids.Where(l => l.Quantity > 0).ToArray();
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

    private static int? GetDenseRowIndexForChange(DirtyLevelChange change, DensePackingLayout layout)
    {
        if (change.Side == Side.ASK)
        {
            var askIndex = IndexOfPrice(layout.NonEmptyAsks, change.Price);
            if (askIndex < 0)
            {
                askIndex = LowerBoundPrice(layout.NonEmptyAsks, change.Price);
            }

            var rowOffset = (layout.NonEmptyAsks.Length - 1 - layout.StartAskIndex) - askIndex;
            if (rowOffset >= 0 && rowOffset < layout.AskRowsToRender)
            {
                return layout.FirstRowIndex + rowOffset;
            }
            return null;
        }

        var bidIndex = IndexOfPrice(layout.NonEmptyBids, change.Price);
        if (bidIndex < 0)
        {
            bidIndex = LowerBoundPrice(layout.NonEmptyBids, change.Price);
        }

        var bidRowOffset = (layout.NonEmptyBids.Length - 1 - layout.StartBidIndex) - bidIndex;
        if (bidRowOffset >= 0 && bidRowOffset < layout.BidRowsToRender)
        {
            return layout.FirstRowIndex + layout.AskRowsToRender + bidRowOffset;
        }

        return null;
    }

    private static bool TryGetDenseLevelForRow(int rowIndex, DensePackingLayout layout, out BookLevel level, out Side side)
    {
        level = default;
        side = default;

        var relativeRow = rowIndex - layout.FirstRowIndex;
        if (relativeRow < 0)
        {
            return false;
        }

        if (relativeRow < layout.AskRowsToRender)
        {
            var askIndex = layout.NonEmptyAsks.Length - 1 - layout.StartAskIndex - relativeRow;
            if (askIndex >= 0 && askIndex < layout.NonEmptyAsks.Length)
            {
                level = layout.NonEmptyAsks[askIndex];
                side = Side.ASK;
                return true;
            }
            return false;
        }

        var bidRow = relativeRow - layout.AskRowsToRender;
        if (bidRow < layout.BidRowsToRender)
        {
            var bidIndex = layout.NonEmptyBids.Length - 1 - layout.StartBidIndex - bidRow;
            if (bidIndex >= 0 && bidIndex < layout.NonEmptyBids.Length)
            {
                level = layout.NonEmptyBids[bidIndex];
                side = Side.BID;
                return true;
            }
        }

        return false;
    }

    private static int IndexOfPrice(BookLevel[] levels, decimal price)
    {
        for (int i = 0; i < levels.Length; i++)
        {
            if (levels[i].Price == price)
            {
                return i;
            }
        }

        return -1;
    }

    private static int LowerBoundPrice(BookLevel[] levels, decimal price)
    {
        var low = 0;
        var high = levels.Length;
        while (low < high)
        {
            var mid = (low + high) / 2;
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

    private void DrawGridLines(SKCanvas canvas, ViewportManager viewport)
    {
        var visibleRows = viewport.Height / RenderConfig.RowHeight;

        for (int i = 0; i <= visibleRows; i++)
        {
            var y = i * RenderConfig.RowHeight;
            canvas.DrawLine(0, y, viewport.Width, y, _gridLinePaint);
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
        long maxVolume,
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
        var paint = side == Side.BID ? _bidVolumePaint : _askVolumePaint;

        double xOffset = 0;  // Track position within virtual segment space
        var gap = RenderConfig.SegmentGapPx;

        for (int i = 0; i < orders.Length; i++)
        {
            var order = orders[i];

            // Calculate proportional width (no min constraint)
            var segmentWidth = order.Quantity * pixelsPerUnit;

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
                var qtyText = order.Quantity.ToString("N0");  // e.g., "1,234,567"
                var textWidth = _segmentTextPaint.MeasureText(qtyText);

                // Only draw text if segment is wide enough and text is in visible area
                if (renderWidth > 40 && textWidth < renderWidth - 4)
                {
                    var textCenterX = segmentStartX + renderWidth / 2;

                    // Check if text center is in visible area
                    if (textCenterX >= barStartX && textCenterX <= barStartX + barColumnWidth)
                    {
                        var textY = y + 4 + barHeight / 2 + 3;  // Vertically centered
                        canvas.DrawText(qtyText, textCenterX, textY, _segmentTextPaint);
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
    }

    private void DrawBidLevel(SKCanvas canvas, BookLevel level, ViewportManager viewport, long maxVolume, float y, Order[]? orders = null)
    {
        // Calculate text baseline (centered vertically in row)
        var textY = y + (RenderConfig.RowHeight / 2) + (_textPaint.TextSize / 3);

        // Column order: Bid Order Count | Bid Qty | Price | Ask Qty | Ask Order Count | Volume Bars
        // BID: Draw bid order count, bid qty, and price only (leave ask columns empty)

        var isEmpty = level.Quantity == 0;

        // Bid order count (if enabled and not empty)
        if (viewport.ShowOrderCount && !isEmpty)
        {
            canvas.DrawText(
                $"({level.NumOrders})",
                viewport.BidOrderCountColumnX + (viewport.ColumnWidth / 2),
                textY,
                _textPaint);
        }

        // Bid quantity (skip if empty)
        if (!isEmpty)
        {
            canvas.DrawText(
                level.Quantity.ToString("N0"),
                viewport.BidQtyColumnX + (viewport.ColumnWidth / 2),
                textY,
                _textPaint);
        }

        // Price (always show, even if empty)
        canvas.DrawText(
            level.Price.ToString("F2"),
            viewport.PriceColumnX + (viewport.ColumnWidth / 2),
            textY,
            _textPaint);

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

    private void DrawAskLevel(SKCanvas canvas, BookLevel level, ViewportManager viewport, long maxVolume, float y, Order[]? orders = null)
    {
        // Calculate text baseline (centered vertically in row)
        var textY = y + (RenderConfig.RowHeight / 2) + (_textPaint.TextSize / 3);

        // Column order: Bid Order Count | Bid Qty | Price | Ask Qty | Ask Order Count | Volume Bars
        // ASK: Draw price, ask qty, and ask order count only (leave bid columns empty)

        var isEmpty = level.Quantity == 0;

        // Price (always show, even if empty)
        canvas.DrawText(
            level.Price.ToString("F2"),
            viewport.PriceColumnX + (viewport.ColumnWidth / 2),
            textY,
            _textPaint);

        // Ask quantity (skip if empty)
        if (!isEmpty)
        {
            canvas.DrawText(
                level.Quantity.ToString("N0"),
                viewport.AskQtyColumnX + (viewport.ColumnWidth / 2),
                textY,
                _textPaint);
        }

        // Ask order count (if enabled and not empty)
        if (viewport.ShowOrderCount && !isEmpty)
        {
            canvas.DrawText(
                $"({level.NumOrders})",
                viewport.AskOrderCountColumnX + (viewport.ColumnWidth / 2),
                textY,
                _textPaint);
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

    private void DrawBidLevelQuantity(SKCanvas canvas, BookLevel level, ViewportManager viewport, long maxVolume, float y, Order[]? orders = null)
    {
        // Draw only quantity data (price already drawn in Step 1)
        var textY = y + (RenderConfig.RowHeight / 2) + (_textPaint.TextSize / 3);
        var isEmpty = level.Quantity == 0;

        // Bid order count (if enabled and not empty)
        if (viewport.ShowOrderCount && !isEmpty)
        {
            canvas.DrawText(
                $"({level.NumOrders})",
                viewport.BidOrderCountColumnX + (viewport.ColumnWidth / 2),
                textY,
                _textPaint);
        }

        // Bid quantity (skip if empty)
        if (!isEmpty)
        {
            canvas.DrawText(
                level.Quantity.ToString("N0"),
                viewport.BidQtyColumnX + (viewport.ColumnWidth / 2),
                textY,
                _textPaint);
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

    private void DrawAskLevelQuantity(SKCanvas canvas, BookLevel level, ViewportManager viewport, long maxVolume, float y, Order[]? orders = null)
    {
        // Draw only quantity data (price already drawn in Step 1)
        var textY = y + (RenderConfig.RowHeight / 2) + (_textPaint.TextSize / 3);
        var isEmpty = level.Quantity == 0;

        // Ask quantity (skip if empty)
        if (!isEmpty)
        {
            canvas.DrawText(
                level.Quantity.ToString("N0"),
                viewport.AskQtyColumnX + (viewport.ColumnWidth / 2),
                textY,
                _textPaint);
        }

        // Ask order count (if enabled and not empty)
        if (viewport.ShowOrderCount && !isEmpty)
        {
            canvas.DrawText(
                $"({level.NumOrders})",
                viewport.AskOrderCountColumnX + (viewport.ColumnWidth / 2),
                textY,
                _textPaint);
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

    private float CalculateVolumeBarWidth(long quantity, long maxVolume, float maxWidth)
    {
        if (maxVolume == 0) return 0;
        return (float)quantity / maxVolume * maxWidth;
    }

    /// <summary>
    /// Calculate base scale factor from max order quantity (mirrors TypeScript calculateBaseScale)
    /// Formula: basePixelsPerUnit = TARGET_MAX_SEGMENT_WIDTH / maxOrderQuantity
    /// </summary>
    private void CalculateBaseScale(OrderBookSnapshot snapshot)
    {
        long maxOrderQty = 0;

        // Check bid orders
        if (snapshot.BidOrders != null)
        {
            foreach (var orders in snapshot.BidOrders.Values)
            {
                foreach (var order in orders)
                {
                    maxOrderQty = Math.Max(maxOrderQty, order.Quantity);
                }
            }
        }

        // Check ask orders
        if (snapshot.AskOrders != null)
        {
            foreach (var orders in snapshot.AskOrders.Values)
            {
                foreach (var order in orders)
                {
                    maxOrderQty = Math.Max(maxOrderQty, order.Quantity);
                }
            }
        }

        if (maxOrderQty > 0)
        {
            var newBaseScale = (double)RenderConfig.TargetMaxSegmentWidth / maxOrderQty;

            var changeRatio = Math.Abs(newBaseScale - _segmentState.BasePixelsPerUnit)
                             / _segmentState.BasePixelsPerUnit;

            // Only update if significantly different (20% threshold) or initial calculation
            if (changeRatio > 0.2 || _segmentState.BasePixelsPerUnit == 1.0)
            {
                _segmentState.BasePixelsPerUnit = newBaseScale;
                // Note: RecalculateMaxScroll would be called here if we add scroll support
            }
        }
    }

    /// <summary>
    /// Recalculate max horizontal scroll offset based on current scale (mirrors TypeScript recalculateMaxScroll)
    /// </summary>
    private void RecalculateMaxScroll(OrderBookSnapshot snapshot, ViewportManager viewport)
    {
        var pixelsPerUnit = _segmentState.BasePixelsPerUnit * _segmentState.UserScaleFactor;
        double maxWidth = 0;

        // Check all price levels for widest segment set
        foreach (var level in snapshot.Bids)
        {
            if (snapshot.BidOrders != null && snapshot.BidOrders.TryGetValue(level.Price, out var orders) && orders.Length > 0)
            {
                double totalWidth = 0;
                foreach (var order in orders)
                {
                    totalWidth += (order.Quantity * pixelsPerUnit) + RenderConfig.SegmentGapPx;
                }
                maxWidth = Math.Max(maxWidth, totalWidth);
            }
        }

        foreach (var level in snapshot.Asks)
        {
            if (snapshot.AskOrders != null && snapshot.AskOrders.TryGetValue(level.Price, out var orders) && orders.Length > 0)
            {
                double totalWidth = 0;
                foreach (var order in orders)
                {
                    totalWidth += (order.Quantity * pixelsPerUnit) + RenderConfig.SegmentGapPx;
                }
                maxWidth = Math.Max(maxWidth, totalWidth);
            }
        }

        _segmentState.MaxScrollOffset = Math.Max(0, maxWidth - viewport.VolumeBarMaxWidth);
    }

    /// <summary>
    /// Adjust segment scale factor (mirrors TypeScript adjustSegmentScale)
    /// </summary>
    public void AdjustSegmentScale(int delta, OrderBookSnapshot snapshot, ViewportManager viewport)
    {
        var step = RenderConfig.SegmentScaleStep;
        var newScale = _segmentState.UserScaleFactor + (delta * step);

        // Clamp to valid range
        _segmentState.UserScaleFactor = Math.Max(
            RenderConfig.SegmentScaleMin,
            Math.Min(RenderConfig.SegmentScaleMax, newScale)
        );

        // Recalculate max scroll offset with new scale
        RecalculateMaxScroll(snapshot, viewport);

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
        _cachedSurface?.Dispose();
    }
}
