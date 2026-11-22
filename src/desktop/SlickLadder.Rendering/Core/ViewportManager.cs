using SlickLadder.Core;
using SlickLadder.Core.Models;

namespace SlickLadder.Rendering.Core;

/// <summary>
/// Manages viewport state, coordinate transformations, and visible level culling.
/// Platform-agnostic - works with both WPF and Avalonia.
/// IMPORTANT: Column layout MUST match canvas-renderer.ts exactly!
/// </summary>
public class ViewportManager
{
    // Viewport dimensions
    public int Width { get; set; }
    public int Height { get; set; }

    // Column layout (MUST match web version exactly!)
    // Order: Work | Bid Qty | Price | Ask Qty | LTQ | Orders, optional ones when enabled (Orders in MBO mode only).
    // Each x is the sum of the widths left of it, so the column fills tile edge to edge
    // Dynamic quantity column width (default to standard column width)
    public float QtyColWidth { get; set; } = RenderConfig.ColumnWidth;
    // Dynamic price column width (default to standard column width)
    public float PriceColWidth { get; set; } = RenderConfig.ColumnWidth;
    // Dynamic Work and LTQ column widths, set by the renderer to fit their cells
    public float WorkColWidth { get; set; } = RenderConfig.ColumnWidth;
    public float LtqColWidth { get; set; } = RenderConfig.ColumnWidth;

    public float? WorkColumnX => ShowWorkColumn ? 0 : null;
    public float BidQtyColumnX => ShowWorkColumn ? WorkColWidth : 0;
    public float PriceColumnX => BidQtyColumnX + QtyColWidth;
    public float AskQtyColumnX => PriceColumnX + PriceColWidth;
    public float? LtqColumnX => ShowLtqColumn ? AskQtyColumnX + QtyColWidth : null;

    public float? OrdersColumnX => OrdersShown ? FixedColumnsWidth : null;

    /// <summary>The Orders (bar) column is on screen: ShowOrders, and an MBO book</summary>
    public bool OrdersShown => ShowOrders && DataMode == DataMode.MBO;

    /// <summary>
    /// Side of the quantity column under x, null outside both
    /// </summary>
    public Side? QtyColumnSideAt(float x)
    {
        if (x >= BidQtyColumnX && x < BidQtyColumnX + QtyColWidth) return Side.BID;
        if (x >= AskQtyColumnX && x < AskQtyColumnX + QtyColWidth) return Side.ASK;
        return null;
    }

    /// <summary>
    /// Right edge of the columns left of the bars: Work through LTQ, all dynamic
    /// </summary>
    public float FixedColumnsWidth => ShowLtqColumn ? LtqColumnX!.Value + LtqColWidth : AskQtyColumnX + QtyColWidth;

    // Responsive bar column width: fills remaining canvas width (min 100px)
    public float OrdersColumnWidth
    {
        get
        {
            if (!OrdersShown) return 0;
            var fixedColumnsWidth = FixedColumnsWidth;
            var barColumnWidth = Math.Max(RenderConfig.MinBarColumnWidth, Width - fixedColumnsWidth);
            return barColumnWidth;
        }
    }

    // Viewport state
    public decimal CenterPrice { get; set; } = 50000.00m;
    public int VisibleLevels { get; set; } = 50;
    public decimal? HoveredPrice { get; set; }

    // Dense packing scroll state (row-based scrolling for RemoveRow mode)
    public int DensePackingScrollOffset { get; set; } = 0;

    // Keep the bid/ask midpoint on the middle row. When off, Space or middle-click re-centers
    public bool AutoCenter { get; set; } = true;

    // Feature toggles
    public bool ShowOrders { get; set; } = true; // The Orders column: each order's bar, right of LTQ, in MBO mode only

    /// <summary>
    /// Data mode of the book drawn, set by the renderer from each snapshot. The Orders column shows for MBO only
    /// </summary>
    public DataMode DataMode { get; set; } = DataMode.PriceLevel;
    public bool ShowOrderCount { get; set; } = false; // In the qty column: "(5) 102" on bids, "102 (5)" on asks
    public bool ShowWorkColumn { get; set; } = true; // Our working orders as "10 (2)", left of Bid Qty
    public bool ShowLtqColumn { get; set; } = true; // The last trade's quantity on its price, right of Ask Qty
    public bool ShowHeader { get; set; } = true; // Column titles above the rows
    public LevelRemovalMode RemovalMode { get; set; } = LevelRemovalMode.RemoveRow;

    // Price configuration
    public decimal TickSize { get; set; } = 0.01m;

    /// <summary>
    /// Display bucket size for aggregation. Must be >= TickSize.
    /// When greater than TickSize, the ladder aggregates levels into coarser buckets.
    /// Default: same as TickSize (no aggregation).
    /// </summary>
    public decimal DisplayTickSize
    {
        get => _displayTickSize;
        set => _displayTickSize = value >= TickSize ? value : TickSize;
    }
    private decimal _displayTickSize = 0.01m;

    // Constants
    private const int RowHeight = RenderConfig.RowHeight;

    /// <summary>Header height in pixels, 0 without one. Rows start below it</summary>
    public int HeaderHeight => ShowHeader ? RowHeight : 0;

    /// <summary>Rows below the header, counting one cut off at the bottom (the renderer's visible rows)</summary>
    public int RowCount => Math.Max(0, (Height - HeaderHeight + RowHeight - 1) / RowHeight);

    /// <summary>Y of a row's top edge</summary>
    public int RowTop(int rowIndex) => HeaderHeight + rowIndex * RowHeight;

    /// <summary>
    /// A Work cell at x
    /// </summary>
    public bool WorkColumnAt(float x) => ShowWorkColumn && x >= 0 && x < WorkColWidth;

    /// <summary>
    /// Price of the level drawn at y, null on the header, an empty row or below the rows. Lays the
    /// rows out as SkiaRenderer does (mirrors rowToLevelInfo in canvas-renderer.ts)
    /// </summary>
    public decimal? LevelPriceAt(in OrderBookSnapshot snapshot, float y) => PriceAt(snapshot, y, rowLabel: false);

    /// <summary>
    /// Price of the row at y as drawn: its level's price, else in Show Empty Rows the row's own
    /// price, which holds working orders even without a level. Null on the header, below the rows
    /// or on a Remove Rows row with no level (mirrors rowPrice in canvas-renderer.ts)
    /// </summary>
    public decimal? RowPriceAt(in OrderBookSnapshot snapshot, float y) => PriceAt(snapshot, y, rowLabel: true);

    // rowLabel: a Show Empty Rows row with no level gives its own price
    private decimal? PriceAt(in OrderBookSnapshot snapshot, float y, bool rowLabel)
    {
        if (y < HeaderHeight)
        {
            return null;
        }
        var rowIndex = (int)((y - HeaderHeight) / RowHeight);
        var rowCount = RowCount;
        if (rowIndex >= rowCount)
        {
            return null;
        }
        var midRow = rowCount / 2;

        if (RemovalMode == LevelRemovalMode.RemoveRow)
        {
            var layout = DensePackingLayout.Build(
                SkiaRenderer.NonEmptyLevels(snapshot.Asks, SkiaRenderer.LevelStats.Scan(snapshot.Asks)),
                SkiaRenderer.NonEmptyLevels(snapshot.Bids, SkiaRenderer.LevelStats.Scan(snapshot.Bids)),
                DensePackingScrollOffset, rowCount, midRow);
            return layout.LevelAt(rowIndex)?.Price;
        }

        // Show Empty Rows: a level is on the row its price maps to, as SkiaRenderer.PlaceLevels puts
        // it, and the row's own price is its label
        var referencePrice = CenterPrice != 0 ? CenterPrice : (snapshot.MidPrice ?? 50000m);
        return LevelOnRow(snapshot.Asks, rowIndex, midRow, referencePrice) ?? LevelOnRow(snapshot.Bids, rowIndex, midRow, referencePrice) ??
            (rowLabel ? Math.Round((referencePrice - (rowIndex - midRow) * DisplayTickSize) / DisplayTickSize) * DisplayTickSize : null);
    }

    private decimal? LevelOnRow(BookLevel[] levels, int rowIndex, int midRow, decimal referencePrice)
    {
        foreach (ref readonly var level in levels.AsSpan())
        {
            if (level.Quantity > 0 && midRow - (int)Math.Round((level.Price - referencePrice) / DisplayTickSize) == rowIndex)
            {
                return level.Price;
            }
        }
        return null;
    }

    /// <summary>
    /// Get the visible price range for viewport culling
    /// </summary>
    public (decimal minPrice, decimal maxPrice) GetVisibleRange()
    {
        var halfLevels = VisibleLevels / 2;
        return (
            CenterPrice - (halfLevels * DisplayTickSize),
            CenterPrice + (halfLevels * DisplayTickSize)
        );
    }

    /// <summary>
    /// Convert price to pixel Y coordinate
    /// Higher prices = lower Y (top of screen), like web version
    /// </summary>
    public float PriceToPixel(decimal price)
    {
        var (minPrice, maxPrice) = GetVisibleRange();
        var offsetFromMax = (maxPrice - price) / DisplayTickSize; // Invert: higher price = lower Y
        return (float)offsetFromMax * RowHeight;
    }

    /// <summary>
    /// Convert pixel Y coordinate to price
    /// </summary>
    public decimal PixelToPrice(double pixelY)
    {
        var (minPrice, maxPrice) = GetVisibleRange();
        var levelIndex = (int)(pixelY / RowHeight);
        return maxPrice - (levelIndex * DisplayTickSize); // Invert: top row = highest price
    }

    /// <summary>
    /// Scroll viewport by specified number of price levels
    /// </summary>
    public void Scroll(int levelsDelta)
    {
        CenterPrice += levelsDelta * DisplayTickSize;
    }

    /// <summary>
    /// Center viewport on specific price
    /// </summary>
    public void CenterOn(decimal price)
    {
        CenterPrice = price;
    }

    /// <summary>
    /// Move the bid/ask midpoint (or the only side present) to the middle row in both removal modes.
    /// Rounds half away from zero, matching snapToDisplayTick in canvas-renderer.ts.
    /// </summary>
    public void Recenter(in OrderBookSnapshot snapshot)
    {
        DensePackingScrollOffset = 0;
        var target = snapshot.MidPrice ?? snapshot.BestBid ?? snapshot.BestAsk;
        if (target.HasValue)
        {
            CenterPrice = Math.Round(target.Value / DisplayTickSize, MidpointRounding.AwayFromZero) * DisplayTickSize;
        }
    }
}
