using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using SkiaSharp;
using SlickLadder.Core.Models;
using SlickLadder.Rendering.Core;
using SlickLadder.Rendering.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace SlickLadder.Core.Tests;

/// <summary>
/// Many ladders open at once, each with its own random-rate feed, on a simulated 60 FPS clock:
/// every frame ingests the updates due since the last frame, then renders every ladder like the
/// WPF PriceLadderControl does (skip idle ladders, update its retained bitmap in place).
/// Scenarios: price levels, MBO orders, and a deep book (500 levels a side). Fails if the average
/// frame exceeds the budget, p95 exceeds 1.5x the budget, or a ladder's book drifts from its feed.
/// Runs marked as measured check the books but not the budget.
/// Single-threaded, so stricter than the controls, where feeds flush on their own threads. Mirrors
/// src/web/test/multi-ladder.perf.test.js. SLICK_PERF_FRAME_BUDGET_MS overrides the budget
/// </summary>
[Collection(nameof(MultiLadderPerformanceTests))]
public class MultiLadderPerformanceTests
{
    private const double FrameMs = 1000.0 / 60;
    private const int WarmupFrames = 60;
    private const int MeasuredFrames = 300;
    private const int Width = 400;
    private const int Height = 600;
    private const decimal TickSize = RandomBookFeed.TickSize;

    private static readonly double FrameBudgetMs = double.TryParse(
        Environment.GetEnvironmentVariable("SLICK_PERF_FRAME_BUDGET_MS"),
        NumberStyles.Float, CultureInfo.InvariantCulture, out var budget) && budget > 0 ? budget : FrameMs;
    private static readonly double P95BudgetMs = FrameBudgetMs * 1.5; // Room for scheduler noise: the average holds the 60 FPS line

    private readonly ITestOutputHelper _output;

    public MultiLadderPerformanceTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    public void RandomRateFeeds_KeepUpAt60Fps(int ladderCount) =>
        RunScenario(ladderCount, seed => new RandomBookFeed(seed), IngestLevels);

    // 100 MBO ladders are measured only: core order ingest alone takes about half the budget
    [Theory]
    [InlineData(50, true)]
    [InlineData(100, false)]
    public void OrderFeeds_KeepUpAt60Fps(int ladderCount, bool heldToBudget) =>
        RunScenario(ladderCount, seed => new RandomOrderFeed(seed), IngestOrders, DataMode.MBO, heldToBudget: heldToBudget);

    // Snapshots carry the levels within 50 ticks of the mid
    [Fact]
    public void DeepBooks_KeepUpAt60Fps() =>
        RunScenario(100, seed => new RandomBookFeed(seed, depth: 500), IngestLevels);

    /// <summary>
    /// Avalonia on a GPU, one ladder per window: Render into a GPU surface per ladder. Each frame
    /// waits for the GPU, so its work counts (reported as "gpu"). Window presents aren't included.
    /// Measured only (the GPU varies by machine), skipped without OpenGL
    /// </summary>
    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    public void GpuTargets_KeepUpAt60Fps(int ladderCount)
    {
        using var gl = GlContext.TryCreate();
        if (gl == null)
        {
            _output.WriteLine("No OpenGL: skipped");
            return;
        }
        RunScenario(ladderCount, seed => new RandomBookFeed(seed), IngestLevels, gpu: gl.Gpu, heldToBudget: false);
    }

    private static void IngestLevels(PriceLadderViewModel viewModel, PriceLevel[] batch) => viewModel.ProcessBatch(batch);

    private static void IngestOrders(PriceLadderViewModel viewModel, (OrderUpdate Update, OrderUpdateType Type)[] batch)
    {
        foreach (var (update, type) in batch)
        {
            viewModel.Core.ProcessOrderUpdateNoFlush(update, type);
        }
        viewModel.Core.Flush();
    }

    private void RunScenario<TUpdate>(int ladderCount, Func<int, RandomFeed<TUpdate>> createFeed,
        Action<PriceLadderViewModel, TUpdate[]> apply, DataMode mode = DataMode.PriceLevel, GRContext? gpu = null,
        bool heldToBudget = true)
    {
        var feeds = Enumerable.Range(0, ladderCount).Select(i => createFeed(1000 + i)).ToArray();
        // Half the ladders in each removal mode so both render paths are covered
        var ladders = feeds.Select((_, i) =>
            new Ladder(i % 2 == 0 ? LevelRemovalMode.RemoveRow : LevelRemovalMode.ShowEmpty, mode, gpu)).ToArray();
        for (var i = 0; i < ladderCount; i++)
        {
            apply(ladders[i].ViewModel, feeds[i].Seed());
        }

        var ingestMs = new List<double>(MeasuredFrames);
        var renderMs = new List<double>(MeasuredFrames);
        var uploadMs = new List<double>(MeasuredFrames);
        var totalMs = new List<double>(MeasuredFrames);
        long updates = 0;
        for (var frame = 0; frame < WarmupFrames + MeasuredFrames; frame++)
        {
            // Generating updates is the network's job, so it stays out of the timing
            var untilMs = (frame + 1) * FrameMs;
            var due = feeds.Select(feed => feed.Advance(untilMs)).ToArray();

            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < ladderCount; i++)
            {
                foreach (var batch in due[i]) apply(ladders[i].ViewModel, batch);
            }
            var ingested = Stopwatch.GetTimestamp();
            foreach (var ladder in ladders) ladder.Render();
            var rendered = Stopwatch.GetTimestamp();
            gpu?.Flush(submit: true, synchronous: true); // Uploads and draws happen here
            var end = Stopwatch.GetTimestamp();

            if (frame >= WarmupFrames)
            {
                ingestMs.Add(Stopwatch.GetElapsedTime(start, ingested).TotalMilliseconds);
                renderMs.Add(Stopwatch.GetElapsedTime(ingested, rendered).TotalMilliseconds);
                uploadMs.Add(Stopwatch.GetElapsedTime(rendered, end).TotalMilliseconds);
                totalMs.Add(Stopwatch.GetElapsedTime(start, end).TotalMilliseconds);
                updates += due.Sum(ticks => ticks.Sum(batch => batch.Length));
            }
        }

        var (ingest, render, upload, total) = (Stats.Of(ingestMs), Stats.Of(renderMs), Stats.Of(uploadMs), Stats.Of(totalMs));
        var seconds = MeasuredFrames / 60.0;
        _output.WriteLine($"{ladderCount} ladders, {updates} updates in {seconds}s ({updates / seconds:F0}/s)");
        _output.WriteLine($"frame avg {total.Avg:F2}ms p95 {total.P95:F2}ms max {total.Max:F2}ms, budget {FrameBudgetMs:F2}ms (p95 {P95BudgetMs:F2}ms)");
        _output.WriteLine($"ingest avg {ingest.Avg:F2}ms p95 {ingest.P95:F2}ms, render avg {render.Avg:F2}ms p95 {render.P95:F2}ms" +
            (gpu == null ? "" : $", gpu avg {upload.Avg:F2}ms p95 {upload.P95:F2}ms"));

        // A timing means nothing if updates were lost or crossed between ladders
        for (var i = 0; i < ladderCount; i++)
        {
            var feed = feeds[i];
            var viewModel = ladders[i].ViewModel;
            var core = viewModel.Core;
            Check(i, "update count", feed.UpdateCount, core.Metrics.TotalUpdatesProcessed);
            Check(i, "bid levels", feed.Bids.Count, core.OrderBook.BidCount);
            Check(i, "ask levels", feed.Asks.Count, core.OrderBook.AskCount);
            Check(i, "best bid", feed.BestBid, core.GetBestBid());
            Check(i, "best ask", feed.BestAsk, core.GetBestAsk());
            // The last frame rendered the current book, not a stale snapshot
            Check(i, "rendered best bid", feed.BestBid, viewModel.CurrentSnapshot!.Value.BestBid);
            ladders[i].Dispose();
        }

        if (heldToBudget)
        {
            Assert.True(total.Avg <= FrameBudgetMs,
                $"average frame {total.Avg:F2}ms over the {FrameBudgetMs:F2}ms budget with {ladderCount} ladders");
            Assert.True(total.P95 <= P95BudgetMs, $"p95 frame {total.P95:F2}ms over {P95BudgetMs:F2}ms with {ladderCount} ladders");
        }
    }

    private static void Check<T>(int ladder, string what, T expected, T actual)
    {
        Assert.True(EqualityComparer<T>.Default.Equals(expected, actual),
            $"ladder {ladder} {what}: expected {expected}, got {actual}");
    }

    private readonly record struct Stats(double Avg, double P95, double Max)
    {
        public static Stats Of(List<double> values)
        {
            var sorted = values.Order().ToArray();
            var p95 = sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(0.95 * sorted.Length) - 1)];
            return new Stats(values.Average(), p95, sorted[^1]);
        }
    }

    /// <summary>
    /// One ladder as a control hosts it: view model, viewport, renderer and its target. Raster: the
    /// WPF SKElement bitmap kept between paints. GPU: the ladder's own Avalonia window
    /// </summary>
    private sealed class Ladder : IDisposable
    {
        private readonly SkiaRenderer _renderer = new(new RenderConfig());
        private readonly SKSurface _surface;
        private readonly bool _gpu;

        public PriceLadderViewModel ViewModel { get; } = new(TickSize);
        public ViewportManager Viewport { get; }

        public Ladder(LevelRemovalMode removalMode, DataMode mode, GRContext? gpu)
        {
            var info = new SKImageInfo(Width, Height);
            (_surface, _gpu) = gpu == null ? (SKSurface.Create(info), false) : (SKSurface.Create(gpu, false, info), true);
            Viewport = new ViewportManager { Width = Width, Height = Height, TickSize = TickSize, RemovalMode = removalMode };
            ViewModel.Core.SetDataMode(mode);
        }

        // A frame tick repaints only when NeedsRender. Raster renders in place, GPU presents the
        // renderer's cache
        public void Render()
        {
            var snapshot = ViewModel.CurrentSnapshot!.Value;
            if (!_renderer.NeedsRender(snapshot, Viewport))
            {
                return;
            }
            if (_gpu)
            {
                _renderer.Render(_surface.Canvas, snapshot, Viewport);
            }
            else
            {
                _renderer.RenderRetained(_surface, snapshot, Viewport);
            }
        }

        public void Dispose()
        {
            _renderer.Dispose();
            _surface.Dispose();
            ViewModel.Dispose();
        }
    }
}

/// <summary>
/// Runs alone (not alongside other test classes) so they don't skew the timings
/// </summary>
[CollectionDefinition(nameof(MultiLadderPerformanceTests), DisableParallelization = true)]
public class MultiLadderPerformanceCollection
{
}
