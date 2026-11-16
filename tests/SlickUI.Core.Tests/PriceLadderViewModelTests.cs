using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using SlickLadder.Core.Models;
using SlickLadder.Rendering.Core;
using SlickLadder.Rendering.ViewModels;
using Xunit;

namespace SlickLadder.Core.Tests;

/// <summary>
/// Spread and level counts are pushed from the snapshot handler instead of WhenAnyValue
/// </summary>
public class PriceLadderViewModelTests
{
    [Fact]
    public void DerivedValues_FollowSnapshots_AndNotifyOnlyOnChange()
    {
        var viewModel = new PriceLadderViewModel();
        Assert.Equal(0m, viewModel.Spread.Value);
        Assert.Equal(0, viewModel.BidLevelCount.Value);

        viewModel.ProcessBatch(new[] { new PriceLevel(Side.BID, 100.00m, 10, 1), new PriceLevel(Side.ASK, 100.03m, 10, 1) });
        Assert.Equal(0.03m, viewModel.Spread.Value);
        Assert.Equal(1, viewModel.BidLevelCount.Value);
        Assert.Equal(1, viewModel.AskLevelCount.Value);

        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        viewModel.ProcessBatch(new[] { new PriceLevel(Side.ASK, 100.01m, 10, 1) });

        Assert.Equal(0.01m, viewModel.Spread.Value);
        Assert.Equal(2, viewModel.AskLevelCount.Value);
        Assert.Equal(new[] { nameof(viewModel.CurrentSnapshot), nameof(viewModel.Spread), nameof(viewModel.AskLevelCount) }, changed);
    }

    // The subjects behind Spread and the level counts were never completed, so their subscribers
    // stayed attached to a discarded view model
    [Fact]
    public void Dispose_CompletesDerivedValues_AndStopsTakingSnapshots()
    {
        var viewModel = new PriceLadderViewModel();
        viewModel.ProcessBatch(new[] { new PriceLevel(Side.BID, 100.00m, 10, 1), new PriceLevel(Side.ASK, 100.03m, 10, 1) });
        var completed = 0;
        foreach (var name in new[] { "_spread", "_bidLevelCount", "_askLevelCount" })
        {
            switch (typeof(PriceLadderViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(viewModel))
            {
                case IObservable<decimal> values: values.Subscribe(_ => { }, () => completed++); break;
                case IObservable<int> values: values.Subscribe(_ => { }, () => completed++); break;
            }
        }

        viewModel.Dispose();
        Assert.Equal(3, completed);

        viewModel.ProcessBatch(new[] { new PriceLevel(Side.ASK, 100.01m, 10, 1) });
        Assert.Single(viewModel.CurrentSnapshot!.Value.Asks);
        viewModel.Dispose(); // Twice is fine
    }

    // Bug: Dispose disposed the subjects, so a flush already in the snapshot handler on the feed
    // thread threw ObjectDisposedException there
    [Fact]
    public void Dispose_DuringAFlushOnTheFeedThread_DoesNotThrowThere()
    {
        var viewModel = new PriceLadderViewModel();
        viewModel.ProcessBatch(new[] { new PriceLevel(Side.BID, 100.00m, 10, 1), new PriceLevel(Side.ASK, 100.03m, 10, 1) });
        using var inHandler = new ManualResetEventSlim();
        using var disposed = new ManualResetEventSlim();
        // Holds the feed thread in the snapshot handler, past the snapshot, before the new spread
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(viewModel.CurrentSnapshot))
            {
                inHandler.Set();
                disposed.Wait();
            }
        };

        Exception? error = null;
        var feed = new Thread(() =>
        {
            try
            {
                viewModel.ProcessBatch(new[] { new PriceLevel(Side.ASK, 100.01m, 10, 1) });
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        feed.Start();
        inHandler.Wait();
        viewModel.Dispose();
        disposed.Set();
        feed.Join();
        Assert.Null(error);
    }
}

/// <summary>
/// RenderMetrics averages the last 60 frame times
/// </summary>
public class RenderMetricsTests
{
    [Fact]
    public void RecordFrame_AveragesTheLast60Frames()
    {
        var metrics = new RenderMetrics();
        var ms = Stopwatch.Frequency / 1000;
        var now = 1000 * ms;
        metrics.RecordFrame(now);
        Assert.Equal(0, metrics.AverageFrameTime);

        for (var i = 0; i < 60; i++)
        {
            metrics.RecordFrame(now += 20 * ms);
        }
        Assert.Equal(20, metrics.AverageFrameTime, 6);
        Assert.Equal(50, metrics.CurrentFps, 6);

        for (var i = 0; i < 30; i++)
        {
            metrics.RecordFrame(now += 10 * ms); // Half the window at 10ms
        }
        Assert.Equal(15, metrics.AverageFrameTime, 6);

        metrics.Reset();
        metrics.RecordFrame(now += 10 * ms);
        metrics.RecordFrame(now += 40 * ms);
        Assert.Equal(40, metrics.AverageFrameTime, 6);
    }
}
