using SlickLadder.Core.Managers;
using SlickLadder.Core.Models;

namespace SlickLadder.Core;

/// <summary>
/// Main orchestrator for the price ladder component.
/// Coordinates order book, batching, and market data processing.
/// Designed for <1ms update latency with 10k+ updates/sec throughput.
/// </summary>
public class PriceLadderCore
{
    private readonly OrderBook _orderBook;
    private readonly UpdateBatcher _batcher;
    private readonly PriceLevelManager _priceLevelManager;
    private readonly MBOManager _mboManager;
    private IMarketDataMode _currentManager;
    private DataMode _currentMode;

    /// <summary>Current data mode (PriceLevel or MBO)</summary>
    public DataMode CurrentMode => _currentMode;

    /// <summary>Access to the order book</summary>
    public OrderBook OrderBook => _orderBook;

    /// <summary>Batching metrics</summary>
    public BatchingMetrics Metrics => _batcher.GetMetrics();

    /// <summary>Event fired when order book snapshot is ready for rendering</summary>
    public event Action<OrderBookSnapshot>? OnSnapshotReady;

    /// <summary>
    /// Set the viewport center price for snapshots (null = use mid price)
    /// Call this when the user scrolls the ladder
    /// </summary>
    public void SetSnapshotViewport(decimal? centerPrice, int visibleLevels = 100)
    {
        _batcher.SnapshotCenterPrice = centerPrice;
        _batcher.SnapshotVisibleLevels = visibleLevels;
    }

    public PriceLadderCore(int maxLevels = 200, int queueCapacity = 4096, decimal tickSize = 0.01m)
    {
        _orderBook = new OrderBook(maxLevels, tickSize);
        _batcher = new UpdateBatcher(_orderBook, queueCapacity);
        _priceLevelManager = new PriceLevelManager(_orderBook);
        _mboManager = new MBOManager(_orderBook);
        _currentManager = _priceLevelManager;
        _currentMode = DataMode.PriceLevel;

        // Forward batch flush events
        _batcher.OnBatchFlushed += snapshot =>
        {
            OnSnapshotReady?.Invoke(snapshot);
        };
    }

    /// <summary>
    /// Process a PriceLevel update in aggregated mode.
    /// Queue for micro-batching to achieve <1ms latency.
    /// </summary>
    public bool ProcessPriceLevelUpdate(PriceLevel update)
    {
        if (_currentMode != DataMode.PriceLevel)
        {
            throw new InvalidOperationException("Not in PriceLevel mode");
        }

        return _batcher.QueueUpdate(update);
    }

    /// <summary>
    /// Process a PriceLevel update without auto-flushing.
    /// Use when processing batches - call Flush() manually after all updates.
    /// </summary>
    public bool ProcessPriceLevelUpdateNoFlush(PriceLevel update)
    {
        if (_currentMode != DataMode.PriceLevel)
        {
            throw new InvalidOperationException("Not in PriceLevel mode");
        }

        return _batcher.QueueUpdateNoFlush(update);
    }

    /// <summary>
    /// Process a PriceLevel update from binary data (zero-copy parsing)
    /// </summary>
    public bool ProcessPriceLevelUpdateBinary(ReadOnlySpan<byte> data)
    {
        if (_currentMode != DataMode.PriceLevel)
        {
            throw new InvalidOperationException("Not in PriceLevel mode");
        }

        // Parse using MemoryMarshal (zero-copy)
        _priceLevelManager.ProcessUpdate(data);

        // Get the parsed update and queue it
        // Note: For true zero-copy, we'd need to restructure this
        // For now, we parse then queue
        var update = System.Runtime.InteropServices.MemoryMarshal.Read<PriceLevel>(data);
        return _batcher.QueueUpdate(update);
    }

    /// <summary>
    /// Process multiple updates in batch
    /// </summary>
    public int ProcessBatch(ReadOnlySpan<PriceLevel> updates)
    {
        if (_currentMode != DataMode.PriceLevel)
        {
            throw new InvalidOperationException("Not in PriceLevel mode");
        }

        return _batcher.QueueBatch(updates);
    }

    /// <summary>
    /// Process an OrderUpdate in MBO mode.
    /// Queue for micro-batching to achieve <1ms latency.
    /// </summary>
    public bool ProcessOrderUpdate(OrderUpdate update, OrderUpdateType type)
    {
        if (_currentMode != DataMode.MBO)
        {
            throw new InvalidOperationException("Not in MBO mode");
        }

        return _batcher.QueueOrderUpdate(update, type);
    }

    /// <summary>
    /// Process an OrderUpdate without auto-flushing.
    /// Use when processing batches - call Flush() manually after all updates.
    /// </summary>
    public bool ProcessOrderUpdateNoFlush(OrderUpdate update, OrderUpdateType type)
    {
        if (_currentMode != DataMode.MBO)
        {
            throw new InvalidOperationException("Not in MBO mode");
        }

        return _batcher.QueueOrderUpdateNoFlush(update, type);
    }

    /// <summary>
    /// Force flush all pending updates immediately.
    /// Use this to ensure updates are processed before rendering.
    /// </summary>
    public void Flush()
    {
        _batcher.FlushBatch();
    }

    /// <summary>
    /// Clear any queued updates without flushing.
    /// Use when stopping a data feed to drop in-flight updates.
    /// </summary>
    public void ClearPendingUpdates()
    {
        _batcher.ClearPending();
    }

    /// <summary>
    /// Switch data mode (PriceLevel vs MBO)
    /// </summary>
    public void SetDataMode(DataMode mode)
    {
        System.Diagnostics.Debug.WriteLine($"PriceLadderCore.SetDataMode CALLED: mode={mode}, _currentMode={_currentMode}");

        if (_currentMode == mode)
        {
            System.Diagnostics.Debug.WriteLine($"PriceLadderCore.SetDataMode: EARLY RETURN - already in {mode} mode");
            return;
        }

        // Pause batching
        System.Diagnostics.Debug.WriteLine("PriceLadderCore.SetDataMode: Pausing batcher");
        _batcher.Pause();
        _batcher.ClearPending();

        // Clear order book + MBO state to avoid stale orders after mode switches
        System.Diagnostics.Debug.WriteLine("PriceLadderCore.SetDataMode: Clearing order book");
        _orderBook.Clear();
        _mboManager.Reset();

        // Switch mode and manager
        _currentMode = mode;
        _currentManager = mode == DataMode.MBO ? _mboManager : _priceLevelManager;
        System.Diagnostics.Debug.WriteLine($"PriceLadderCore.SetDataMode: Switched to {mode}, _currentManager={_currentManager?.GetType().Name}, calling batcher.SetDataMode");
        _batcher.SetDataMode(mode, _mboManager);

        // Resume batching
        System.Diagnostics.Debug.WriteLine("PriceLadderCore.SetDataMode: Resuming batcher");
        _batcher.Resume();

        System.Diagnostics.Debug.WriteLine("PriceLadderCore.SetDataMode: DONE");
    }

    /// <summary>
    /// Get current order book snapshot
    /// </summary>
    public OrderBookSnapshot GetSnapshot(decimal centerPrice, int visibleLevels = 50)
    {
        return _orderBook.GetSnapshot(centerPrice, visibleLevels);
    }

    /// <summary>
    /// Mark a price level as having user's own orders
    /// </summary>
    public void MarkOwnOrder(decimal price, Side side, bool hasOwnOrder = true)
    {
        _orderBook.MarkOwnOrder(price, side, hasOwnOrder);
    }

    /// <summary>
    /// Get best bid price
    /// </summary>
    public decimal? GetBestBid() => _orderBook.BestBid;

    /// <summary>
    /// Get best ask price
    /// </summary>
    public decimal? GetBestAsk() => _orderBook.BestAsk;

    /// <summary>
    /// Get mid price
    /// </summary>
    public decimal? GetMidPrice() => _orderBook.MidPrice;

    /// <summary>
    /// Get spread (best ask - best bid)
    /// </summary>
    public decimal? GetSpread()
    {
        if (_orderBook.BestBid.HasValue && _orderBook.BestAsk.HasValue)
        {
            return _orderBook.BestAsk.Value - _orderBook.BestBid.Value;
        }
        return null;
    }

    /// <summary>
    /// Reset the price ladder (clear all data)
    /// </summary>
    public void Reset()
    {
        _batcher.Pause();
        _batcher.ClearPending();
        _orderBook.Clear();
        _mboManager.Reset();
        _batcher.ResetStatistics();
        _batcher.Resume();
    }

    /// <summary>
    /// Get top N bid levels
    /// </summary>
    public ReadOnlySpan<BookLevel> GetTopBids(int count = 10)
    {
        return _orderBook.GetTopBids(count);
    }

    /// <summary>
    /// Get top N ask levels
    /// </summary>
    public ReadOnlySpan<BookLevel> GetTopAsks(int count = 10)
    {
        return _orderBook.GetTopAsks(count);
    }
}

/// <summary>
/// Data mode for market data processing
/// </summary>
public enum DataMode
{
    /// <summary>Aggregated price level updates</summary>
    PriceLevel,

    /// <summary>Market-By-Order (individual order updates)</summary>
    MBO
}
