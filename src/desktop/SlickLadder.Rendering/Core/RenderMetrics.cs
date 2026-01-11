using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using SlickLadder.Core;

namespace SlickLadder.Rendering.Core;

/// <summary>
/// Tracks rendering performance metrics (FPS, frame times).
/// </summary>
public class RenderMetrics
{
    private readonly Queue<long> _frameTimes = new();
    private readonly int _windowSize = 60; // Track last 60 frames (1 second at 60 FPS)
    private long _lastFrameTimestamp;
    private long _renderTraceFrameId;

    public double CurrentFps { get; private set; }
    public double AverageFrameTime { get; private set; } // in milliseconds
    public bool TraceRenderTimings { get; set; } = ReadTraceSetting();
    public double SlowFrameThresholdMs { get; set; } = ReadSlowThresholdSetting();

    public void RecordFrame()
    {
        var now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        if (_lastFrameTimestamp > 0)
        {
            var frameDuration = now - _lastFrameTimestamp;
            _frameTimes.Enqueue(frameDuration);

            // Keep only last N frames
            while (_frameTimes.Count > _windowSize)
            {
                _frameTimes.Dequeue();
            }

            // Calculate metrics
            if (_frameTimes.Count > 0)
            {
                AverageFrameTime = _frameTimes.Average();
                CurrentFps = AverageFrameTime > 0 ? 1000.0 / AverageFrameTime : 0;
            }
        }

        _lastFrameTimestamp = now;
    }

    public void RecordRenderTime(long elapsedTicks, in OrderBookSnapshot snapshot)
    {
        if (!TraceRenderTimings)
        {
            return;
        }

        var elapsedMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;
        if (SlowFrameThresholdMs > 0 && elapsedMs < SlowFrameThresholdMs)
        {
            return;
        }

        var bidLevels = snapshot.Bids?.Length ?? 0;
        var askLevels = snapshot.Asks?.Length ?? 0;
        var bidOrderLevels = snapshot.BidOrders?.Count ?? 0;
        var askOrderLevels = snapshot.AskOrders?.Count ?? 0;
        var totalOrders = 0;

        if (bidOrderLevels > 0 && snapshot.BidOrders != null)
        {
            foreach (var orders in snapshot.BidOrders.Values)
            {
                totalOrders += orders.Length;
            }
        }

        if (askOrderLevels > 0 && snapshot.AskOrders != null)
        {
            foreach (var orders in snapshot.AskOrders.Values)
            {
                totalOrders += orders.Length;
            }
        }

        _renderTraceFrameId++;
        Debug.WriteLine($"RenderTrace frame={_renderTraceFrameId} renderMs={elapsedMs:F3} bids={bidLevels} asks={askLevels} bidOrderLevels={bidOrderLevels} askOrderLevels={askOrderLevels} totalOrders={totalOrders}");
    }

    public void Reset()
    {
        _frameTimes.Clear();
        _lastFrameTimestamp = 0;
        CurrentFps = 0;
        AverageFrameTime = 0;
    }

    private static bool ReadTraceSetting()
    {
        var value = Environment.GetEnvironmentVariable("SLICKLADDER_TRACE_RENDER");
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static double ReadSlowThresholdSetting()
    {
        var value = Environment.GetEnvironmentVariable("SLICKLADDER_TRACE_RENDER_THRESHOLD_MS");
        if (string.IsNullOrWhiteSpace(value))
        {
            return 10.0;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold))
        {
            return threshold;
        }

        return 10.0;
    }
}
