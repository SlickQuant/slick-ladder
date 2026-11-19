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
/// Level clicks carry the mouse button, and a handler can mark them handled (an unhandled
/// middle-click on a qty column re-centers)
/// </summary>
public class LevelClickTests
{
    [Theory]
    [InlineData(ClickButton.Left)]
    [InlineData(ClickButton.Middle)]
    [InlineData(ClickButton.Right)]
    public void HandlePriceClick_PassesButton(ClickButton button)
    {
        var viewModel = new PriceLadderViewModel();
        LevelClickEventArgs? received = null;
        viewModel.OnLevelClick += args => received = args;

        viewModel.HandlePriceClick(100.01m, Side.ASK, button);

        Assert.NotNull(received);
        Assert.Equal(100.01m, received!.Price);
        Assert.Equal(Side.ASK, received.Side);
        Assert.Equal(button, received.Button);
    }

    [Fact]
    public void HandlePriceClick_DefaultsToLeft()
    {
        var viewModel = new PriceLadderViewModel();
        LevelClickEventArgs? received = null;
        viewModel.OnLevelClick += args => received = args;

        viewModel.HandlePriceClick(null, Side.BID);

        Assert.Equal(ClickButton.Left, received!.Button);
        Assert.Null(received.Price);
    }

    [Fact]
    public void HandlePriceClick_ReturnsHandled()
    {
        var viewModel = new PriceLadderViewModel();
        Assert.False(viewModel.HandlePriceClick(100m, Side.BID, ClickButton.Middle)); // No subscriber

        viewModel.OnLevelClick += _ => { };
        Assert.False(viewModel.HandlePriceClick(100m, Side.BID, ClickButton.Middle));

        // Any handler of the multicast event can set it
        viewModel.OnLevelClick += args => args.Handled = args.Button == ClickButton.Middle;
        viewModel.OnLevelClick += _ => { };
        Assert.True(viewModel.HandlePriceClick(100m, Side.BID, ClickButton.Middle));
        Assert.False(viewModel.HandlePriceClick(100m, Side.BID, ClickButton.Left));
    }

    // Order count shares the qty column, so the columns are Bid Qty | Price | Ask Qty | Bars either way
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QtyColumnSideAt_MatchesColumnBounds(bool showOrderCount)
    {
        var viewport = new ViewportManager { ShowOrderCount = showOrderCount, QtyColWidth = 80, PriceColWidth = 70 };
        Assert.Equal(0, viewport.BidQtyColumnX);
        Assert.Equal(150, viewport.AskQtyColumnX);
        Assert.Equal(230, viewport.VolumeBarColumnX);

        Assert.Equal(Side.BID, viewport.QtyColumnSideAt(0));
        Assert.Equal(Side.BID, viewport.QtyColumnSideAt(79.9f));
        Assert.Null(viewport.QtyColumnSideAt(80)); // Price column
        Assert.Equal(Side.ASK, viewport.QtyColumnSideAt(150));
        Assert.Equal(Side.ASK, viewport.QtyColumnSideAt(229.9f));
        Assert.Null(viewport.QtyColumnSideAt(230)); // Bars
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
