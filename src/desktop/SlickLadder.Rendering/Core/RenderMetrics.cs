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
    // Last 60 frame durations (1 second at 60 FPS) in a ring, with their running sum. Every UI
    // frame records one, idle or not, so the average never walks the window
    private readonly double[] _frameTimes = new double[60];
    private int _frameCount;
    private int _nextFrame;
    private double _frameTimeSum;
    private long _lastFrameTimestamp;
    private long _renderTraceFrameId;

    public double CurrentFps { get; private set; }
    public double AverageFrameTime { get; private set; } // in milliseconds
    public bool TraceRenderTimings { get; set; } = ReadTraceSetting();
    public double SlowFrameThresholdMs { get; set; } = ReadSlowThresholdSetting();

    public void RecordFrame() => RecordFrame(Stopwatch.GetTimestamp());

    /// <summary>
    /// A frame at a <see cref="Stopwatch.GetTimestamp"/> timestamp, for a caller that already has
    /// one (a compositor frame time, or a test clock)
    /// </summary>
    public void RecordFrame(long timestamp)
    {
        if (_lastFrameTimestamp != 0)
        {
            var frameMs = (timestamp - _lastFrameTimestamp) * 1000.0 / Stopwatch.Frequency;
            if (_frameCount == _frameTimes.Length)
            {
                _frameTimeSum -= _frameTimes[_nextFrame]; // The oldest frame leaves the window
            }
            else
            {
                _frameCount++;
            }
            _frameTimes[_nextFrame] = frameMs;
            _frameTimeSum += frameMs;
            _nextFrame = (_nextFrame + 1) % _frameTimes.Length;

            AverageFrameTime = _frameTimeSum / _frameCount;
            CurrentFps = AverageFrameTime > 0 ? 1000.0 / AverageFrameTime : 0;
        }

        _lastFrameTimestamp = timestamp;
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
        _frameCount = 0;
        _nextFrame = 0;
        _frameTimeSum = 0;
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
