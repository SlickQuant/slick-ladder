using System;
using System.Collections.Generic;
using System.Linq;

namespace SlickLadder.Rendering.Core;

/// <summary>
/// Tracks rendering performance metrics (FPS, frame times).
/// </summary>
public class RenderMetrics
{
    private readonly Queue<long> _frameTimes = new();
    private readonly int _windowSize = 60; // Track last 60 frames (1 second at 60 FPS)
    private long _lastFrameTimestamp;

    public double CurrentFps { get; private set; }
    public double AverageFrameTime { get; private set; } // in milliseconds

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

    public void Reset()
    {
        _frameTimes.Clear();
        _lastFrameTimestamp = 0;
        CurrentFps = 0;
        AverageFrameTime = 0;
    }
}
