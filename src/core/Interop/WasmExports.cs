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

    private static void ExecuteSafely(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WASM] {name} threw: {ex}");
            throw;
        }
    }

    private static T ExecuteSafely<T>(string name, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WASM] {name} threw: {ex}");
            throw;
        }
    }

    /// <summary>
    /// Initialize the price ladder
    /// </summary>
    [JSExport]
    public static void Initialize(int maxLevels, double tickSize)
    {
        ExecuteSafely(nameof(Initialize), () =>
        {
            _ladder = new PriceLadderCore(maxLevels, 4096, (decimal)tickSize);

            // Subscribe to snapshot events
            _ladder.OnSnapshotReady += snapshot =>
            {
                _latestSnapshot = WasmHelpers.SerializeSnapshot(snapshot);
                _hasNewSnapshot = true;
            };
        });
    }

    /// <summary>
    /// Check if a new snapshot is available
    /// </summary>
    [JSExport]
    public static bool HasNewSnapshot()
    {
        return ExecuteSafely(nameof(HasNewSnapshot), () => _hasNewSnapshot);
    }

    /// <summary>
    /// Get the latest snapshot (returns empty string if none available)
    /// </summary>
    [JSExport]
    public static string GetLatestSnapshot()
    {
        return ExecuteSafely(nameof(GetLatestSnapshot), () =>
        {
            _hasNewSnapshot = false;
            return _latestSnapshot ?? "{}";
        });
    }

    /// <summary>
    /// Process a single price level update
    /// </summary>
    [JSExport]
    public static void ProcessPriceLevelUpdate(int side, double price, int quantity, int numOrders)
    {
        ExecuteSafely(nameof(ProcessPriceLevelUpdate), () =>
        {
            if (_ladder == null) return;

            var update = new PriceLevel(
                (Side)side,
                (decimal)price,
                quantity,
                numOrders
            );

            _ladder.ProcessPriceLevelUpdate(update);
        });
    }

    /// <summary>
    /// Process a single price level update without auto-flushing
    /// </summary>
    [JSExport]
    public static void ProcessPriceLevelUpdateNoFlush(int side, double price, int quantity, int numOrders)
    {
        ExecuteSafely(nameof(ProcessPriceLevelUpdateNoFlush), () =>
        {
            if (_ladder == null) return;

            var update = new PriceLevel(
                (Side)side,
                (decimal)price,
                quantity,
                numOrders
            );

            _ladder.ProcessPriceLevelUpdateNoFlush(update);
        });
    }

    /// <summary>
    /// Flush pending updates
    /// </summary>
    [JSExport]
    public static void Flush()
    {
        ExecuteSafely(nameof(Flush), () =>
        {
            _ladder?.Flush();
        });
    }

    /// <summary>
    /// Set data mode (0 = PriceLevel, 1 = MBO)
    /// </summary>
    [JSExport]
    public static void SetDataMode(int mode)
    {
        ExecuteSafely(nameof(SetDataMode), () =>
        {
            if (_ladder == null) return;
            _ladder.SetDataMode((DataMode)mode);
        });
    }

    /// <summary>
    /// Process an order update in MBO mode
    /// </summary>
    [JSExport]
    public static void ProcessOrderUpdate(
        int orderId,
        int side,
        double price,
        int quantity,
        int priority,
        int updateType)
    {
        ExecuteSafely(nameof(ProcessOrderUpdate), () =>
        {
            if (_ladder == null) return;

            var update = new OrderUpdate(
                orderId,
                (Side)side,
                (decimal)price,
                quantity,
                priority
            );

            _ladder.ProcessOrderUpdate(update, (OrderUpdateType)updateType);
        });
    }

    /// <summary>
    /// Process an order update in MBO mode without auto-flushing
    /// </summary>
    [JSExport]
    public static void ProcessOrderUpdateNoFlush(
        int orderId,
        int side,
        double price,
        int quantity,
        int priority,
        int updateType)
    {
        ExecuteSafely(nameof(ProcessOrderUpdateNoFlush), () =>
        {
            if (_ladder == null) return;

            var update = new OrderUpdate(
                orderId,
                (Side)side,
                (decimal)price,
                quantity,
                priority
            );

            _ladder.ProcessOrderUpdateNoFlush(update, (OrderUpdateType)updateType);
        });
    }

    /// <summary>
    /// Get best bid price
    /// </summary>
    [JSExport]
    public static double GetBestBid()
    {
        return ExecuteSafely(nameof(GetBestBid), () =>
        {
            var bid = _ladder?.GetBestBid();
            return bid.HasValue ? (double)bid.Value : 0.0;
        });
    }

    /// <summary>
    /// Get best ask price
    /// </summary>
    [JSExport]
    public static double GetBestAsk()
    {
        return ExecuteSafely(nameof(GetBestAsk), () =>
        {
            var ask = _ladder?.GetBestAsk();
            return ask.HasValue ? (double)ask.Value : 0.0;
        });
    }

    /// <summary>
    /// Get mid price
    /// </summary>
    [JSExport]
    public static double GetMidPrice()
    {
        return ExecuteSafely(nameof(GetMidPrice), () =>
        {
            var mid = _ladder?.GetMidPrice();
            return mid.HasValue ? (double)mid.Value : 0;
        });
    }

    /// <summary>
    /// Get spread
    /// </summary>
    [JSExport]
    public static double GetSpread()
    {
        return ExecuteSafely(nameof(GetSpread), () =>
        {
            var spread = _ladder?.GetSpread();
            return spread.HasValue ? (double)spread.Value : 0;
        });
    }

    /// <summary>
    /// Get number of bid levels
    /// </summary>
    [JSExport]
    public static int GetBidCount()
    {
        return ExecuteSafely(nameof(GetBidCount), () => _ladder?.OrderBook.BidCount ?? 0);
    }

    /// <summary>
    /// Get number of ask levels
    /// </summary>
    [JSExport]
    public static int GetAskCount()
    {
        return ExecuteSafely(nameof(GetAskCount), () => _ladder?.OrderBook.AskCount ?? 0);
    }

    /// <summary>
    /// Clear all data
    /// </summary>
    [JSExport]
    public static void Clear()
    {
        ExecuteSafely(nameof(Clear), () =>
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
        });
    }

    /// <summary>
    /// Clear queued updates without changing order book state
    /// </summary>
    [JSExport]
    public static void ClearPendingUpdates()
    {
        ExecuteSafely(nameof(ClearPendingUpdates), () =>
        {
            _ladder?.ClearPendingUpdates();
        });
    }

    /// <summary>
    /// Get performance metrics as JSON
    /// </summary>
    [JSExport]
    public static string GetMetrics()
    {
        return ExecuteSafely(nameof(GetMetrics), () =>
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
        });
    }

    /// <summary>
    /// WASM entry point (required for browser-wasm compilation)
    /// </summary>
    public static void Main()
    {
        // Entry point for WASM module - no-op as initialization happens via JSExport methods
    }
}
