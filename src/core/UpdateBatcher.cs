using System.Diagnostics;
using System.Linq;
using SlickLadder.Core.DataStructures;
using SlickLadder.Core.Models;

namespace SlickLadder.Core;

/// <summary>
/// Micro-batching engine for ultra-low latency updates.
/// Batches updates in 100μs windows to achieve <1ms processing latency
/// while maintaining 60 FPS rendering.
/// </summary>
public class UpdateBatcher
{
    private const int BATCH_INTERVAL_MICROSECONDS = 100; // 100μs micro-batches
    private const int MAX_BATCH_SIZE = 1000; // Auto-flush if exceeded
    private const long TICKS_PER_MICROSECOND = 10; // 100ns ticks per microsecond
    private const bool EnableDebugLogging = false;

    private readonly RingBuffer<PriceLevel> _updateQueue;
    private readonly RingBuffer<(OrderUpdate, OrderUpdateType)> _orderQueue;
    private readonly OrderBook _orderBook;
    private readonly Stopwatch _batchTimer;
    private long _lastFlushTicks;
    private int _pendingUpdateCount;
    private bool _isPaused;
    private DataMode _mode;
    private Managers.MBOManager? _mboManager;

    /// <summary>Total updates processed</summary>
    public long TotalUpdatesProcessed { get; private set; }

    /// <summary>Total batches flushed</summary>
    public long TotalBatchesFlushed { get; private set; }

    /// <summary>Average updates per batch</summary>
    public double AverageUpdatesPerBatch =>
        TotalBatchesFlushed > 0 ? (double)TotalUpdatesProcessed / TotalBatchesFlushed : 0;

    /// <summary>Whether batching is paused</summary>
    public bool IsPaused => _isPaused;

    /// <summary>Snapshot center price (null = use mid price)</summary>
    public decimal? SnapshotCenterPrice { get; set; }

    /// <summary>Snapshot visible levels</summary>
    public int SnapshotVisibleLevels { get; set; } = 100;

    /// <summary>Fill empty price levels in snapshot (for continuous ladder display)</summary>
    public bool FillEmptyLevels { get; set; } = false;

    /// <summary>Event fired when a batch is flushed</summary>
    public event Action<OrderBookSnapshot>? OnBatchFlushed;

    public UpdateBatcher(OrderBook orderBook, int queueCapacity = 4096)
    {
        _orderBook = orderBook;
        _updateQueue = new RingBuffer<PriceLevel>(queueCapacity);
        _orderQueue = new RingBuffer<(OrderUpdate, OrderUpdateType)>(queueCapacity);
        _batchTimer = Stopwatch.StartNew();
        _lastFlushTicks = _batchTimer.ElapsedTicks;
        _pendingUpdateCount = 0;
        _isPaused = false;
        _mode = DataMode.PriceLevel;
    }

    /// <summary>
    /// Queue an update for batched processing.
    /// Returns true if queued, false if queue is full.
    /// </summary>
    public bool QueueUpdate(PriceLevel update)
    {
        if (_isPaused)
        {
            return false;
        }

        if (!_updateQueue.TryWrite(update))
        {
            // Queue full, force flush and retry
            FlushBatch();
            return _updateQueue.TryWrite(update);
        }

        _pendingUpdateCount++;

        // Check if we should flush
        var elapsedTicks = _batchTimer.ElapsedTicks - _lastFlushTicks;
        var elapsedMicroseconds = elapsedTicks / TICKS_PER_MICROSECOND;

        if (_pendingUpdateCount >= MAX_BATCH_SIZE || elapsedMicroseconds >= BATCH_INTERVAL_MICROSECONDS)
        {
            FlushBatch();
        }

        return true;
    }

    /// <summary>
    /// Queue an update without auto-flushing.
    /// Use this when processing batches and you'll manually flush at the end.
    /// Returns true if queued, false if queue is full.
    /// </summary>
    public bool QueueUpdateNoFlush(PriceLevel update)
    {
        if (_isPaused)
        {
            return false;
        }

        if (!_updateQueue.TryWrite(update))
        {
            // Queue full
            return false;
        }

        _pendingUpdateCount++;
        return true;
    }

    /// <summary>
    /// Queue multiple updates in batch
    /// </summary>
    public int QueueBatch(ReadOnlySpan<PriceLevel> updates)
    {
        if (_isPaused)
        {
            return 0;
        }

        var queued = 0;
        foreach (var update in updates)
        {
            if (QueueUpdate(update))
            {
                queued++;
            }
            else
            {
                break;
            }
        }

        return queued;
    }

    /// <summary>
    /// Queue an OrderUpdate for batched processing (MBO mode).
    /// Returns true if queued, false if queue is full.
    /// </summary>
    public bool QueueOrderUpdate(OrderUpdate update, OrderUpdateType type)
    {
        if (_isPaused || _mode != DataMode.MBO)
        {
            return false;
        }

        if (!_orderQueue.TryWrite((update, type)))
        {
            // Queue full, force flush and retry
            FlushBatch();
            return _orderQueue.TryWrite((update, type));
        }

        _pendingUpdateCount++;

        // Check if we should flush
        var elapsedTicks = _batchTimer.ElapsedTicks - _lastFlushTicks;
        var elapsedMicroseconds = elapsedTicks / TICKS_PER_MICROSECOND;

        if (_pendingUpdateCount >= MAX_BATCH_SIZE || elapsedMicroseconds >= BATCH_INTERVAL_MICROSECONDS)
        {
            FlushBatch();
        }

        return true;
    }

    /// <summary>
    /// Queue an OrderUpdate without auto-flushing (MBO mode).
    /// Use this when processing batches and you'll manually flush at the end.
    /// Returns true if queued, false if queue is full.
    /// </summary>
    public bool QueueOrderUpdateNoFlush(OrderUpdate update, OrderUpdateType type)
    {
        if (_isPaused || _mode != DataMode.MBO)
        {
            return false;
        }

        if (!_orderQueue.TryWrite((update, type)))
        {
            // Queue full
            return false;
        }

        _pendingUpdateCount++;
        return true;
    }

    /// <summary>
    /// Set data mode (PriceLevel vs MBO).
    /// </summary>
    public void SetDataMode(DataMode mode, Managers.MBOManager? mboManager)
    {
        _mode = mode;
        _mboManager = mboManager;
    }

    /// <summary>
    /// Force flush all pending updates immediately.
    /// Call this to ensure all updates are processed.
    /// </summary>
    public void FlushBatch()
    {
        DebugLog($"UpdateBatcher.FlushBatch CALLED: _pendingUpdateCount={_pendingUpdateCount}, TotalBatchesFlushed={TotalBatchesFlushed}, _mode={_mode}");

        if (_pendingUpdateCount == 0)
        {
            DebugLog("UpdateBatcher.FlushBatch: EARLY RETURN - no pending updates");
            return;
        }

        var flushedCount = 0;

        if (_mode == DataMode.MBO)
        {
            DebugLog($"UpdateBatcher.FlushBatch: Processing MBO mode, _mboManager={(object?)_mboManager != null}");
            // MBO mode: Process order updates
            while (_orderQueue.TryRead(out var item))
            {
                var (update, type) = item;
                _mboManager?.ProcessOrderUpdate(update, type);
                flushedCount++;
            }
            DebugLog($"UpdateBatcher.FlushBatch: Flushed {flushedCount} MBO orders");
        }
        else
        {
            DebugLog("UpdateBatcher.FlushBatch: Processing PriceLevel mode");
            // PriceLevel mode: Process price level updates
            while (_updateQueue.TryRead(out var update))
            {
                _orderBook.UpdateLevel(update);
                flushedCount++;
            }
            DebugLog($"UpdateBatcher.FlushBatch: Flushed {flushedCount} price levels");
        }

        // Update statistics
        TotalUpdatesProcessed += flushedCount;
        TotalBatchesFlushed++;

        // Get snapshot of dirty levels for rendering
        // Use configured center price or fall back to mid price rounded to tick
        // Round mid price to nearest tick to avoid fractional ticks (50000.005 -> 50000.00)
        var midPrice = _orderBook.MidPrice;
        if (!midPrice.HasValue)
        {
            midPrice = _orderBook.BestBid ?? _orderBook.BestAsk ?? 0m;
        }

        var centerPrice = SnapshotCenterPrice ?? RoundToTick(midPrice.Value, _orderBook.TickSize);
        var snapshot = _orderBook.GetSnapshot(
            centerPrice,
            SnapshotVisibleLevels,
            FillEmptyLevels
        );
        snapshot.DirtyChanges = _orderBook.GetDirtyChanges();
        snapshot.StructuralChange = _orderBook.HasStructuralChange;

        // In MBO mode, add individual orders to snapshot
        if (_mode == DataMode.MBO && _mboManager != null)
        {
            snapshot.BidOrders = _mboManager.GetBidOrders();
            snapshot.AskOrders = _mboManager.GetAskOrders();

            // Debug: Log first 10 flushes to see when orders appear
            if (TotalBatchesFlushed <= 10)
            {
                if (snapshot.BidOrders == null && snapshot.AskOrders == null)
                {
                    DebugLog($"UpdateBatcher.FlushBatch #{TotalBatchesFlushed}: MBO mode, but BidOrders and AskOrders are BOTH NULL");
                }
                else if (snapshot.BidOrders != null && snapshot.AskOrders != null)
                {
                    var totalOrders = snapshot.BidOrders.Values.Sum(orders => orders.Length) +
                                      snapshot.AskOrders.Values.Sum(orders => orders.Length);
                    DebugLog($"UpdateBatcher.FlushBatch #{TotalBatchesFlushed}: MBO mode, BidOrders={snapshot.BidOrders.Count} levels, AskOrders={snapshot.AskOrders.Count} levels, totalOrders={totalOrders}");
                }
                else
                {
                    DebugLog($"UpdateBatcher.FlushBatch #{TotalBatchesFlushed}: MBO mode, BidOrders={(snapshot.BidOrders != null ? snapshot.BidOrders.Count.ToString() : "NULL")}, AskOrders={(snapshot.AskOrders != null ? snapshot.AskOrders.Count.ToString() : "NULL")}");
                }
            }
        }
        else
        {
            // Debug: Log if not in MBO mode (first 10 flushes)
            if (TotalBatchesFlushed <= 10)
            {
                DebugLog($"UpdateBatcher.FlushBatch #{TotalBatchesFlushed}: mode={_mode}, mboManager={(object?)_mboManager != null}");
            }
        }

        // Clear dirty flags after snapshot
        _orderBook.ClearDirtyFlags();

        // Reset counters
        _pendingUpdateCount = 0;
        _lastFlushTicks = _batchTimer.ElapsedTicks;

        // Notify listeners
        OnBatchFlushed?.Invoke(snapshot);
    }

    /// <summary>
    /// Start automatic batching with a background timer.
    /// Flushes every BATCH_INTERVAL_MICROSECONDS.
    /// </summary>
    public void StartAutoBatching()
    {
        _isPaused = false;

        // Note: For true <1ms latency, we don't use Timer (too coarse).
        // Instead, call FlushBatch() from the main update loop or
        // rely on the automatic flush in QueueUpdate().
    }

    /// <summary>
    /// Pause batching (for mode switching, etc.)
    /// </summary>
    public void Pause()
    {
        FlushBatch(); // Flush remaining updates
        _isPaused = true;
    }

    /// <summary>
    /// Resume batching
    /// </summary>
    public void Resume()
    {
        _isPaused = false;
        _lastFlushTicks = _batchTimer.ElapsedTicks;
    }

    /// <summary>
    /// Reset all statistics
    /// </summary>
    public void ResetStatistics()
    {
        TotalUpdatesProcessed = 0;
        TotalBatchesFlushed = 0;
    }

    /// <summary>
    /// Clear queued updates without flushing.
    /// Call only when no producer is writing to the queues.
    /// </summary>
    public void ClearPending()
    {
        _updateQueue.Clear();
        _orderQueue.Clear();
        _pendingUpdateCount = 0;
        _lastFlushTicks = _batchTimer.ElapsedTicks;
    }

    /// <summary>
    /// Get current batching performance metrics
    /// </summary>
    public BatchingMetrics GetMetrics()
    {
        return new BatchingMetrics
        {
            TotalUpdatesProcessed = TotalUpdatesProcessed,
            TotalBatchesFlushed = TotalBatchesFlushed,
            AverageUpdatesPerBatch = AverageUpdatesPerBatch,
            PendingUpdateCount = _pendingUpdateCount,
            QueueUtilization = (double)_updateQueue.Count / _updateQueue.Capacity
        };
    }

    /// <summary>
    /// Round price to nearest tick (0.01). Rounds down to avoid fractional ticks.
    /// Example: 50000.005 -> 50000.00
    /// </summary>
    private static decimal RoundToTick(decimal price, decimal tickSize)
    {
        return Math.Floor(price / tickSize) * tickSize;
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void DebugLog(string message)
    {
        if (!EnableDebugLogging)
        {
            return;
        }

        System.Diagnostics.Debug.WriteLine(message);
    }
}

/// <summary>
/// Performance metrics for the update batcher
/// </summary>
public struct BatchingMetrics
{
    public long TotalUpdatesProcessed;
    public long TotalBatchesFlushed;
    public double AverageUpdatesPerBatch;
    public int PendingUpdateCount;
    public double QueueUtilization;
}
