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
    private DataMode _currentMode;

    /// <summary>Current data mode (PriceLevel or MBO)</summary>
    public DataMode CurrentMode => _currentMode;

    /// <summary>Access to the order book</summary>
    public OrderBook OrderBook => _orderBook;

    /// <summary>Batching metrics</summary>
    public BatchingMetrics Metrics => _batcher.GetMetrics();

    /// <summary>Event fired when order book snapshot is ready for rendering</summary>
    public event Action<OrderBookSnapshot>? OnSnapshotReady;

    public PriceLadderCore(int maxLevels = 200, int queueCapacity = 4096)
    {
        _orderBook = new OrderBook(maxLevels);
        _batcher = new UpdateBatcher(_orderBook, queueCapacity);
        _priceLevelManager = new PriceLevelManager(_orderBook);
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
    /// Force flush all pending updates immediately.
    /// Use this to ensure updates are processed before rendering.
    /// </summary>
    public void Flush()
    {
        _batcher.FlushBatch();
    }

    /// <summary>
    /// Switch data mode (PriceLevel vs MBO)
    /// </summary>
    public void SetDataMode(DataMode mode)
    {
        if (_currentMode == mode)
        {
            return;
        }

        // Pause batching
        _batcher.Pause();

        // Clear order book
        _orderBook.Clear();

        // Switch mode
        _currentMode = mode;

        // Resume batching
        _batcher.Resume();
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
        _orderBook.Clear();
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
