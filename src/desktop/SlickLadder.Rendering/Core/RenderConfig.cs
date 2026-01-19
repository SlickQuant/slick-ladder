using SkiaSharp;

namespace SlickLadder.Rendering.Core;

/// <summary>
/// Configuration for rendering colors, fonts, and layout.
/// Matches the web version color scheme for visual consistency.
/// </summary>
public class RenderConfig
{
    // Background colors (MUST match web version DEFAULT_COLORS exactly)
    public SKColor BackgroundColor { get; } = new SKColor(30, 30, 30);      // #1e1e1e
    public SKColor BidBackground { get; } = new SKColor(26, 47, 58);        // #1a2f3a (very dark blue - barely visible)
    public SKColor AskBackground { get; } = new SKColor(58, 26, 31);        // #3a1a1f (very dark red - barely visible)
    public SKColor PriceBackground { get; } = new SKColor(58, 58, 58);      // #3a3a3a (medium gray)
    public SKColor OrderCountBackground { get; } = new SKColor(30, 30, 30); // #1e1e1e (same as main background - matches web)

    // Text colors
    public SKColor TextColor { get; } = SKColors.White;

    // Grid and borders
    public SKColor GridLineColor { get; } = new SKColor(68, 68, 68);        // #444444

    // Volume bar colors (MUST match web version)
    public SKColor BidVolumeBar { get; } = new SKColor(76, 175, 80);        // #4caf50 (green)
    public SKColor AskVolumeBar { get; } = new SKColor(244, 67, 54);        // #f44336 (red)

    // Debug overlay for dirty rows
    public bool DebugDirtyRows { get; set; } = false;
    public SKColor DirtyRowOverlayColor { get; set; } = new SKColor(255, 255, 255, 20); // ~8% white

    // Font settings
    public string FontFamily { get; } = "Consolas";
    public float FontSize { get; } = 12.0f;

    // Order segment gap (MBO volume bars)
    public float OrderSegmentGap { get; } = 2.0f;
    public float MinOrderSegmentWidth { get; } = 30.0f;  // Increased from 2.0f for better visibility and quantity text

    // Own order border color (must match web version ownOrderBorder)
    public SKColor OwnOrderBorderColor { get; } = new SKColor(255, 215, 0);  // #ffd700 (gold)

    // Layout constants (must match web version exactly)
    public const int RowHeight = 24;
    public const float ColumnWidth = 66.7f;  // Matches web version COL_WIDTH
    public const float VolumeBarWidthMultiplier = 2.5f;

    // New segment rendering configuration (must match web version)
    public const double SegmentScaleMin = 0.1;
    public const double SegmentScaleMax = 1000.0;
    public const double SegmentScaleStep = 0.25; // Base step for dynamic zoom
    public const int MinSegmentWidthPx = 1;
    public const int SegmentGapPx = 2;
    public const int MinBarColumnWidth = 100;
    public const int TargetMaxSegmentWidth = 200;
}

/// <summary>
/// State for segment rendering with dynamic scaling and scrolling
/// Mirrors TypeScript SegmentRenderState interface
/// </summary>
public class SegmentRenderState
{
    public double BasePixelsPerUnit { get; set; } = 1.0;  // Auto-calculated from max order quantity
    public double UserScaleFactor { get; set; } = 1.0;    // 1.0 default (0.1 to 1000.0 range)
    public double HorizontalScrollOffset { get; set; } = 0;  // Global scroll in pixels
    public double MaxScrollOffset { get; set; } = 0;      // Calculated from widest level
}

/// <summary>
/// How to handle price levels when quantity becomes zero
/// </summary>
public enum LevelRemovalMode
{
    /// <summary>Keep the level row, show only price, hide qty/orders/bar</summary>
    ShowEmpty,

    /// <summary>Completely remove the row when quantity = 0</summary>
    RemoveRow
}
