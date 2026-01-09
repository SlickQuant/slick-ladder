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
    // Order: Bid Order Count | Bid Qty | Price | Ask Qty | Ask Order Count | Volume Bars
    public float ColumnWidth => RenderConfig.ColumnWidth;

    // Column X positions (calculated dynamically based on ShowOrderCount)
    private int ColumnCount => ShowOrderCount ? 5 : 3; // 5 columns with order count, 3 without

    public float BidOrderCountColumnX => 0; // Always column 0
    public float BidQtyColumnX => ShowOrderCount ? ColumnWidth : 0;
    public float PriceColumnX => ShowOrderCount ? ColumnWidth * 2 : ColumnWidth;
    public float AskQtyColumnX => ShowOrderCount ? ColumnWidth * 3 : ColumnWidth * 2;
    public float AskOrderCountColumnX => ColumnWidth * 4; // Always column 4

    public float? VolumeBarColumnX => ShowVolumeBars ? ColumnWidth * ColumnCount : null;
    public float VolumeBarMaxWidth => RenderConfig.ColumnWidth - 5; // Matches web: COL_WIDTH - 5

    // Viewport state
    public decimal CenterPrice { get; set; } = 50000.00m;
    public int VisibleLevels { get; set; } = 50;
    public decimal? HoveredPrice { get; set; }

    // Dense packing scroll state (row-based scrolling for RemoveRow mode)
    public int DensePackingScrollOffset { get; set; } = 0;

    // Feature toggles
    public bool ShowVolumeBars { get; set; } = true;
    public bool ShowOrderCount { get; set; } = false;
    public LevelRemovalMode RemovalMode { get; set; } = LevelRemovalMode.RemoveRow;

    // Price configuration
    public decimal TickSize { get; set; } = 0.01m;

    // Constants
    private const int RowHeight = RenderConfig.RowHeight;

    /// <summary>
    /// Get the visible price range for viewport culling
    /// </summary>
    public (decimal minPrice, decimal maxPrice) GetVisibleRange()
    {
        var halfLevels = VisibleLevels / 2;
        return (
            CenterPrice - (halfLevels * TickSize),
            CenterPrice + (halfLevels * TickSize)
        );
    }

    /// <summary>
    /// Convert price to pixel Y coordinate
    /// Higher prices = lower Y (top of screen), like web version
    /// </summary>
    public float PriceToPixel(decimal price)
    {
        var (minPrice, maxPrice) = GetVisibleRange();
        var offsetFromMax = (maxPrice - price) / TickSize; // Invert: higher price = lower Y
        return (float)offsetFromMax * RowHeight;
    }

    /// <summary>
    /// Convert pixel Y coordinate to price
    /// </summary>
    public decimal PixelToPrice(double pixelY)
    {
        var (minPrice, maxPrice) = GetVisibleRange();
        var levelIndex = (int)(pixelY / RowHeight);
        return maxPrice - (levelIndex * TickSize); // Invert: top row = highest price
    }

    /// <summary>
    /// Scroll viewport by specified number of price levels
    /// </summary>
    public void Scroll(int levelsDelta)
    {
        CenterPrice += levelsDelta * TickSize;
    }

    /// <summary>
    /// Center viewport on specific price
    /// </summary>
    public void CenterOn(decimal price)
    {
        CenterPrice = price;
    }
}
