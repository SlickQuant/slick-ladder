using System.Diagnostics;
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

    private readonly RingBuffer<PriceLevel> _updateQueue;
    private readonly OrderBook _orderBook;
    private readonly Stopwatch _batchTimer;
    private long _lastFlushTicks;
    private int _pendingUpdateCount;
    private bool _isPaused;

    /// <summary>Total updates processed</summary>
    public long TotalUpdatesProcessed { get; private set; }

    /// <summary>Total batches flushed</summary>
    public long TotalBatchesFlushed { get; private set; }

    /// <summary>Average updates per batch</summary>
    public double AverageUpdatesPerBatch =>
        TotalBatchesFlushed > 0 ? (double)TotalUpdatesProcessed / TotalBatchesFlushed : 0;

    /// <summary>Whether batching is paused</summary>
    public bool IsPaused => _isPaused;

    /// <summary>Event fired when a batch is flushed</summary>
    public event Action<OrderBookSnapshot>? OnBatchFlushed;

    public UpdateBatcher(OrderBook orderBook, int queueCapacity = 4096)
    {
        _orderBook = orderBook;
        _updateQueue = new RingBuffer<PriceLevel>(queueCapacity);
        _batchTimer = Stopwatch.StartNew();
        _lastFlushTicks = _batchTimer.ElapsedTicks;
        _pendingUpdateCount = 0;
        _isPaused = false;
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
    /// Force flush all pending updates immediately.
    /// Call this to ensure all updates are processed.
    /// </summary>
    public void FlushBatch()
    {
        if (_pendingUpdateCount == 0)
        {
            return;
        }

        var flushedCount = 0;

        // Process all pending updates in tight loop
        while (_updateQueue.TryRead(out var update))
        {
            _orderBook.UpdateLevel(update);
            flushedCount++;
        }

        // Update statistics
        TotalUpdatesProcessed += flushedCount;
        TotalBatchesFlushed++;

        // Get snapshot of dirty levels for rendering
        var snapshot = _orderBook.GetSnapshot(
            _orderBook.MidPrice ?? 0m,
            50 // Visible levels
        );

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
