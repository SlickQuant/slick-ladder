using SkiaSharp;
using SlickLadder.Core;
using SlickLadder.Core.Models;
using System;
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
    private readonly SKPaint _textPaint;
    private readonly SKPaint _bidVolumePaint;
    private readonly SKPaint _askVolumePaint;
    private readonly SKPaint _gridLinePaint;
    private readonly SKPaint _highlightPaint;

    private readonly RenderConfig _config;

    public SkiaRenderer(RenderConfig config)
    {
        _config = config;

        // Initialize reusable paints (critical for 60 FPS zero-allocation)
        _bidBackgroundPaint = new SKPaint { Color = _config.BidBackground, Style = SKPaintStyle.Fill };
        _askBackgroundPaint = new SKPaint { Color = _config.AskBackground, Style = SKPaintStyle.Fill };
        _priceBackgroundPaint = new SKPaint { Color = _config.PriceBackground, Style = SKPaintStyle.Fill };

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
    }

    /// <summary>
    /// Main render method - platform-agnostic, works on WPF and Avalonia
    /// Uses price-to-row mapping with viewport scroll offset support
    /// </summary>
    public void Render(SKCanvas canvas, OrderBookSnapshot snapshot, ViewportManager viewport)
    {
        // Clear background
        canvas.Clear(_config.BackgroundColor);

        // Draw column backgrounds
        DrawColumnBackgrounds(canvas, viewport);

        // Draw grid lines
        DrawGridLines(canvas, viewport);

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
        const decimal tickSize = 0.01m;

        if (viewport.RemovalMode == LevelRemovalMode.RemoveRow)
        {
            // DENSE PACKING MODE: Render levels consecutively without gaps
            // Filter out empty levels and pack remaining levels together
            var nonEmptyAsks = snapshot.Asks.Where(l => l.Quantity > 0).ToArray();
            var nonEmptyBids = snapshot.Bids.Where(l => l.Quantity > 0).ToArray();

            // Use row-based scroll offset for dense packing
            // scrollOffset directly controls position: 0 = centered at spread
            // scrollOffset > 0: scrolled down (show more bids)
            // scrollOffset < 0: scrolled up (show more asks)
            var scrollOffset = viewport.DensePackingScrollOffset;

            // Both arrays sorted low-to-high (index 0 = lowest price, end = highest price)
            // Strategy: Keep the spread (ask/bid boundary) stable at midRow when scrollOffset = 0
            // The spread should always be at the center of the viewport regardless of data changes

            var totalLevels = nonEmptyAsks.Length + nonEmptyBids.Length;

            // At scrollOffset = 0, the spread is at midRow
            // startAskIndex should show the last few asks (highest prices) above midRow
            // startBidIndex should show the first few bids (highest prices) below midRow
            // scrollOffset adjusts from this centered position
            var virtualTopRow = nonEmptyAsks.Length - midRow + scrollOffset;

            // Clamp virtualTopRow to allow continuous scrolling with empty space
            // Allow scrolling beyond data boundaries
            var virtualBottomRow = virtualTopRow + visibleRows;

            int startAskIndex, askRowsToRender;
            int startBidIndex, bidRowsToRender;
            float topOffset = 0;

            // Calculate which part of asks/bids to show based on virtual scroll position
            if (virtualTopRow < 0)
            {
                // Scrolled above all data, empty space at top
                topOffset = -virtualTopRow * RenderConfig.RowHeight;
                startAskIndex = 0;
                askRowsToRender = Math.Min(nonEmptyAsks.Length, Math.Max(0, visibleRows + virtualTopRow));
                startBidIndex = 0;
                var bidRowsAvailable = Math.Max(0, visibleRows + virtualTopRow - nonEmptyAsks.Length);
                bidRowsToRender = Math.Min(nonEmptyBids.Length, bidRowsAvailable);
            }
            else if (virtualTopRow < nonEmptyAsks.Length)
            {
                // Showing some asks at top
                startAskIndex = virtualTopRow;
                askRowsToRender = Math.Min(nonEmptyAsks.Length - startAskIndex, visibleRows);
                startBidIndex = 0;
                var bidRowsAvailable = visibleRows - askRowsToRender;
                bidRowsToRender = Math.Min(nonEmptyBids.Length, bidRowsAvailable);
            }
            else if (virtualTopRow < totalLevels)
            {
                // Past all asks, showing only bids
                startAskIndex = nonEmptyAsks.Length;
                askRowsToRender = 0;
                var bidStartRow = virtualTopRow - nonEmptyAsks.Length;
                startBidIndex = bidStartRow;
                bidRowsToRender = Math.Min(nonEmptyBids.Length - startBidIndex, visibleRows);
            }
            else
            {
                // Scrolled past all data, empty space at bottom
                startAskIndex = nonEmptyAsks.Length;
                askRowsToRender = 0;
                startBidIndex = nonEmptyBids.Length;
                bidRowsToRender = 0;
            }

            // Render asks: highest ask at top, lowest ask (closest to mid) at bottom of ask section
            float currentY = topOffset;
            for (int i = 0; i < askRowsToRender; i++)
            {
                var askIndex = nonEmptyAsks.Length - 1 - startAskIndex - i; // Start from highest remaining ask
                if (askIndex >= 0 && askIndex < nonEmptyAsks.Length)
                {
                    var y = currentY + (i * RenderConfig.RowHeight);

                    // Only render if visible on screen
                    if (y >= 0 && y < viewport.Height)
                    {
                        DrawAskLevel(canvas, nonEmptyAsks[askIndex], viewport, maxVolume, y);
                    }
                }
            }

            // Render bids: highest bid (closest to mid) at top of bid section, going down
            currentY = topOffset + (askRowsToRender * RenderConfig.RowHeight);
            for (int i = 0; i < bidRowsToRender; i++)
            {
                var bidIndex = nonEmptyBids.Length - 1 - startBidIndex - i; // Start from highest bid
                if (bidIndex >= 0 && bidIndex < nonEmptyBids.Length)
                {
                    var y = currentY + (i * RenderConfig.RowHeight);

                    // Only render if visible on screen
                    if (y >= 0 && y < viewport.Height)
                    {
                        DrawBidLevel(canvas, nonEmptyBids[bidIndex], viewport, maxVolume, y);
                    }
                }
            }
        }
        else
        {
            // PRICE-TO-ROW MAPPING MODE: Each price maps to fixed row (shows gaps for empty levels)
            // Determine reference price (viewport center or mid market)
            var referencePrice = viewport.CenterPrice != 0
                ? viewport.CenterPrice
                : (snapshot.MidPrice ?? 50000m);

            // Render all ask levels, mapping price to screen row
            foreach (var level in snapshot.Asks)
            {
                // Calculate row: higher prices appear at lower row indices (top of screen)
                var priceDelta = level.Price - referencePrice;
                var rowOffset = -(int)Math.Round(priceDelta / tickSize);
                var rowIndex = midRow + rowOffset;

                // Only render if visible on screen
                if (rowIndex >= 0 && rowIndex <= visibleRows)
                {
                    var y = rowIndex * RenderConfig.RowHeight;
                    DrawAskLevel(canvas, level, viewport, maxVolume, y);
                }
            }

            // Render all bid levels, mapping price to screen row
            foreach (var level in snapshot.Bids)
            {
                // Calculate row: higher prices appear at lower row indices (top of screen)
                var priceDelta = level.Price - referencePrice;
                var rowOffset = -(int)Math.Round(priceDelta / tickSize);
                var rowIndex = midRow + rowOffset;

                // Only render if visible on screen
                if (rowIndex >= 0 && rowIndex <= visibleRows)
                {
                    var y = rowIndex * RenderConfig.RowHeight;
                    DrawBidLevel(canvas, level, viewport, maxVolume, y);
                }
            }
        }
    }

    private void DrawColumnBackgrounds(SKCanvas canvas, ViewportManager viewport)
    {
        // Column order: Bid Order Count | Bid Qty | Price | Ask Qty | Ask Order Count | Volume Bars

        // Bid order count column (blue background, same as bid qty)
        if (viewport.ShowOrderCount)
        {
            canvas.DrawRect(
                viewport.BidOrderCountColumnX, 0,
                viewport.ColumnWidth, viewport.Height,
                _bidBackgroundPaint);
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

        // Ask order count column (red background, same as ask qty)
        if (viewport.ShowOrderCount)
        {
            canvas.DrawRect(
                viewport.AskOrderCountColumnX, 0,
                viewport.ColumnWidth, viewport.Height,
                _askBackgroundPaint);
        }
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

    private void DrawBidLevel(SKCanvas canvas, BookLevel level, ViewportManager viewport, long maxVolume, float y)
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

        // Draw volume bar (if enabled and not empty)
        if (viewport.ShowVolumeBars && viewport.VolumeBarColumnX.HasValue && maxVolume > 0 && !isEmpty)
        {
            var barWidth = CalculateVolumeBarWidth(level.Quantity, maxVolume, viewport.VolumeBarMaxWidth);

            canvas.DrawRect(
                viewport.VolumeBarColumnX.Value,
                y + 4,
                barWidth,
                RenderConfig.RowHeight - 8,
                _bidVolumePaint);
        }
    }

    private void DrawAskLevel(SKCanvas canvas, BookLevel level, ViewportManager viewport, long maxVolume, float y)
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

        // Draw volume bar (if enabled and not empty)
        if (viewport.ShowVolumeBars && viewport.VolumeBarColumnX.HasValue && maxVolume > 0 && !isEmpty)
        {
            var barWidth = CalculateVolumeBarWidth(level.Quantity, maxVolume, viewport.VolumeBarMaxWidth);

            canvas.DrawRect(
                viewport.VolumeBarColumnX.Value,
                y + 4,
                barWidth,
                RenderConfig.RowHeight - 8,
                _askVolumePaint);
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

    public void Dispose()
    {
        _bidBackgroundPaint?.Dispose();
        _askBackgroundPaint?.Dispose();
        _priceBackgroundPaint?.Dispose();
        _textPaint?.Dispose();
        _bidVolumePaint?.Dispose();
        _askVolumePaint?.Dispose();
        _gridLinePaint?.Dispose();
        _highlightPaint?.Dispose();
    }
}
