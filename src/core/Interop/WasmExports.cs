using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using SlickLadder.Core;
using SlickLadder.Core.Models;

namespace SlickLadder.Core.Interop;

/// <summary>
/// WASM JavaScript interop exports for SlickLadder
/// </summary>
[SupportedOSPlatform("browser")]
public partial class WasmExports
{
    private static PriceLadderCore? _ladder;
    private static string? _latestSnapshot;
    private static bool _hasNewSnapshot;

    /// <summary>
    /// Initialize the price ladder
    /// </summary>
    [JSExport]
    public static void Initialize(int maxLevels)
    {
        _ladder = new PriceLadderCore(maxLevels);

        // Subscribe to snapshot events
        _ladder.OnSnapshotReady += snapshot =>
        {
            _latestSnapshot = WasmHelpers.SerializeSnapshot(snapshot);
            _hasNewSnapshot = true;
        };
    }

    /// <summary>
    /// Check if a new snapshot is available
    /// </summary>
    [JSExport]
    public static bool HasNewSnapshot()
    {
        return _hasNewSnapshot;
    }

    /// <summary>
    /// Get the latest snapshot (returns empty string if none available)
    /// </summary>
    [JSExport]
    public static string GetLatestSnapshot()
    {
        _hasNewSnapshot = false;
        return _latestSnapshot ?? "{}";
    }

    /// <summary>
    /// Process a single price level update
    /// </summary>
    [JSExport]
    public static void ProcessPriceLevelUpdate(int side, double price, int quantity, int numOrders)
    {
        if (_ladder == null) return;

        var update = new PriceLevel(
            (Side)side,
            (decimal)price,
            quantity,
            numOrders
        );

        _ladder.ProcessPriceLevelUpdate(update);
    }

    /// <summary>
    /// Process a single price level update without auto-flushing
    /// </summary>
    [JSExport]
    public static void ProcessPriceLevelUpdateNoFlush(int side, double price, int quantity, int numOrders)
    {
        if (_ladder == null) return;

        var update = new PriceLevel(
            (Side)side,
            (decimal)price,
            quantity,
            numOrders
        );

        _ladder.ProcessPriceLevelUpdateNoFlush(update);
    }

    /// <summary>
    /// Flush pending updates
    /// </summary>
    [JSExport]
    public static void Flush()
    {
        _ladder?.Flush();
    }

    /// <summary>
    /// Get best bid price
    /// </summary>
    [JSExport]
    public static double GetBestBid()
    {
        var bid = _ladder?.GetBestBid();
        return bid.HasValue ? (double)bid.Value : 0.0;
    }

    /// <summary>
    /// Get best ask price
    /// </summary>
    [JSExport]
    public static double GetBestAsk()
    {
        var ask = _ladder?.GetBestAsk();
        return ask.HasValue ? (double)ask.Value : 0.0;
    }

    /// <summary>
    /// Get mid price
    /// </summary>
    [JSExport]
    public static double GetMidPrice()
    {
        var mid = _ladder?.GetMidPrice();
        return mid.HasValue ? (double)mid.Value : 0;
    }

    /// <summary>
    /// Get spread
    /// </summary>
    [JSExport]
    public static double GetSpread()
    {
        var spread = _ladder?.GetSpread();
        return spread.HasValue ? (double)spread.Value : 0;
    }

    /// <summary>
    /// Get number of bid levels
    /// </summary>
    [JSExport]
    public static int GetBidCount()
    {
        return _ladder?.OrderBook.BidCount ?? 0;
    }

    /// <summary>
    /// Get number of ask levels
    /// </summary>
    [JSExport]
    public static int GetAskCount()
    {
        return _ladder?.OrderBook.AskCount ?? 0;
    }

    /// <summary>
    /// Clear all data
    /// </summary>
    [JSExport]
    public static void Clear()
    {
        if (_ladder == null) return;

        Console.WriteLine("[WASM] Clear() called - resetting ladder");
        _ladder.Reset();

        // Trigger a snapshot with empty data so the UI updates
        var emptySnapshot = _ladder.GetSnapshot(0, 0);
        Console.WriteLine($"[WASM] Empty snapshot: bids={emptySnapshot.Bids.Length}, asks={emptySnapshot.Asks.Length}");
        _latestSnapshot = WasmHelpers.SerializeSnapshot(emptySnapshot);
        _hasNewSnapshot = true;
        Console.WriteLine("[WASM] Snapshot flagged as ready");
    }

    /// <summary>
    /// Get performance metrics as JSON
    /// </summary>
    [JSExport]
    public static string GetMetrics()
    {
        if (_ladder == null) return "{}";

        // Manual JSON serialization to avoid reflection
        var sb = new System.Text.StringBuilder();
        sb.Append("{");

        sb.Append($"\"bidLevels\":{_ladder.OrderBook.BidCount},");
        sb.Append($"\"askLevels\":{_ladder.OrderBook.AskCount},");

        var bestBid = _ladder.GetBestBid();
        sb.Append("\"bestBid\":");
        sb.Append(bestBid.HasValue ? ((double)bestBid.Value).ToString("G17") : "0");
        sb.Append(",");

        var bestAsk = _ladder.GetBestAsk();
        sb.Append("\"bestAsk\":");
        sb.Append(bestAsk.HasValue ? ((double)bestAsk.Value).ToString("G17") : "0");
        sb.Append(",");

        var midPrice = _ladder.GetMidPrice();
        sb.Append("\"midPrice\":");
        sb.Append(midPrice.HasValue ? ((double)midPrice.Value).ToString("G17") : "null");
        sb.Append(",");

        var spread = _ladder.GetSpread();
        sb.Append("\"spread\":");
        sb.Append(spread.HasValue ? ((double)spread.Value).ToString("G17") : "null");

        sb.Append("}");

        return sb.ToString();
    }

    /// <summary>
    /// WASM entry point (required for browser-wasm compilation)
    /// </summary>
    public static void Main()
    {
        // Entry point for WASM module - no-op as initialization happens via JSExport methods
    }
}
