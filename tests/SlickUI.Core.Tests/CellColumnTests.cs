using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using SkiaSharp;
using SlickLadder.Core.Managers;
using SlickLadder.Core.Models;
using SlickLadder.Rendering.Core;
using SlickLadder.Rendering.Simulation;
using SlickLadder.Rendering.ViewModels;
using Xunit;

namespace SlickLadder.Core.Tests;

/// <summary>
/// Work column (our working orders), LTQ column (last trade), the header row, and clicks landing
/// on the rows as drawn. Mirrors src/web/test/cells.test.js
/// </summary>
public class CellColumnTests
{
    private const int Width = 400;

    private static PriceLadderCore Core(out Func<OrderBookSnapshot> snapshot)
    {
        var core = new PriceLadderCore();
        OrderBookSnapshot last = default;
        core.OnSnapshotReady += s => last = s;
        snapshot = () => last;
        return core;
    }

    [Fact]
    public void SetWorkingOrders_EmitsAndClears()
    {
        var core = Core(out var snapshot);
        core.SetWorkingOrders(100m, Side.BID, 10, 2);
        var first = snapshot().BidWork!;
        Assert.Equal(new WorkingOrders(10, 2), first[100m]);
        Assert.Null(snapshot().AskWork);

        core.SetWorkingOrders(100.01m, Side.BID, 5, 1);
        core.SetWorkingOrders(100m, Side.BID, 0, 0);
        Assert.Equal(new[] { 100.01m }, snapshot().BidWork!.Keys);
        Assert.Single(first); // A snapshot's map never changes

        core.SetWorkingOrders(100.01m, Side.BID, 0, 0);
        Assert.Null(snapshot().BidWork);
    }

    // FlushBatch returns early when nothing is queued, so a trade counts as a pending update
    [Fact]
    public void ProcessTrade_TradeOnlyFlushEmits()
    {
        var core = Core(out var snapshot);
        Assert.True(core.ProcessTrade(100.5m, 7, Side.ASK));
        core.Flush();
        Assert.Equal(new Trade(100.5m, 7, Side.ASK), snapshot().LastTrade);
    }

    [Fact]
    public void Reset_ClearsWorkAndTrade()
    {
        var core = Core(out _);
        core.SetWorkingOrders(100m, Side.ASK, 10, 1);
        core.ProcessTrade(100m, 7);
        core.Reset();
        Assert.Null(core.OrderBook.AskWork);
        Assert.Null(core.OrderBook.LastTrade);
    }

    // Copy-on-write with compare-and-swap: two threads setting orders at once lose none
    [Fact]
    public void SetWorkingOrders_FromTwoThreads_LosesNothing()
    {
        var book = new OrderBook();
        using var start = new Barrier(2);
        var threads = Enumerable.Range(0, 2).Select(t => new Thread(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < 2000; i++) book.SetWorkingOrders(t * 1000 + i * 0.01m, Side.BID, 1, 1);
        })).ToArray();
        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();
        Assert.Equal(4000, book.BidWork!.Count);
    }

    [Fact]
    public void DisplayTick_BucketsWorkLikeItsSide_AndTheTradeByItsAggressor()
    {
        var viewModel = new PriceLadderViewModel(0.01m) { DisplayTickSize = 0.1m };
        viewModel.Core.SetWorkingOrders(100.03m, Side.BID, 1, 1);
        viewModel.Core.SetWorkingOrders(100.07m, Side.BID, 2, 1);
        viewModel.Core.SetWorkingOrders(100.13m, Side.ASK, 4, 2);
        var snapshot = viewModel.CurrentSnapshot!.Value;
        Assert.Equal(new WorkingOrders(3, 2), Assert.Single(snapshot.BidWork!, w => w.Key == 100.0m).Value);
        Assert.Equal(new WorkingOrders(4, 2), Assert.Single(snapshot.AskWork!, w => w.Key == 100.2m).Value);

        decimal Bucket(decimal price, Side? aggressor)
        {
            viewModel.Core.ProcessTrade(price, 1, aggressor);
            viewModel.Core.Flush();
            return viewModel.CurrentSnapshot!.Value.LastTrade!.Price;
        }
        Assert.Equal(100.2m, Bucket(100.13m, Side.BID)); // Buyer lifted an ask: up like asks
        Assert.Equal(100.1m, Bucket(100.13m, Side.ASK)); // Seller hit a bid: down like bids
        Assert.Equal(100.1m, Bucket(100.13m, null)); // Unknown: nearest
        Assert.Equal(100.2m, Bucket(100.15m, null)); // Half away from zero, like the web
        Assert.Equal(100.3m, Bucket(100.3m, Side.ASK)); // On a bucket edge it stays, like the web
        Assert.Equal(100.3m, Bucket(100.3m, Side.BID));
    }

    // Bug: the Work column fit the largest quantity's text, but a smaller quantity can have longer
    // text, so "0.12345678 (1)" overflowed a column sized for "1 (1)"
    [Fact]
    public void WorkColumn_FitsItsWidestCell()
    {
        var core = Core(out var snapshot);
        core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(Side.BID, 100m, 10, 1));
        core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(Side.BID, 99.99m, 10, 1));
        core.Flush();
        core.SetWorkingOrders(100m, Side.BID, 1, 1);
        core.SetWorkingOrders(99.99m, Side.BID, 0.12345678m, 1);

        var config = new RenderConfig { MinQuantityThreshold = 0.00000001m };
        using var renderer = new SkiaRenderer(config);
        using var surface = SKSurface.Create(new SKImageInfo(Width, 600));
        var viewport = new ViewportManager { Width = Width, Height = 600 };
        renderer.Render(surface.Canvas, snapshot(), viewport);

        using var font = new SKFont(SKTypeface.FromFamilyName(config.FontFamily), config.FontSize);
        Assert.Equal((float)Math.Ceiling(font.MeasureText("0.12345678 (1)") + 20), viewport.WorkColWidth);
    }

    public static TheoryData<LevelRemovalMode, decimal, int, bool> ClickLayouts()
    {
        var data = new TheoryData<LevelRemovalMode, decimal, int, bool>();
        foreach (var (mode, displayTick) in new[] { (LevelRemovalMode.RemoveRow, 0.01m), (LevelRemovalMode.ShowEmpty, 0.01m),
            (LevelRemovalMode.ShowEmpty, 0.1m) })
        {
            // Body heights that are and aren't whole rows
            foreach (var (height, header) in new[] { (600, true), (610, true), (600, false), (610, false) })
            {
                data.Add(mode, displayTick, height, header);
            }
        }
        return data;
    }

    // Bug: the WPF and Avalonia controls each laid out the rows again to hit-test a click, with
    // floor rows where the renderer uses ceil, and moved the click up a row to make up for it. When
    // the body was whole rows, a Remove Rows click landed one row above. Show Empty Rows clicks used
    // TickSize, not DisplayTickSize, so aggregated rows found no level. Now every row's click finds
    // the level the renderer drew there
    [Theory]
    [MemberData(nameof(ClickLayouts))]
    public void LevelPriceAt_FindsTheLevelDrawnOnTheRow(LevelRemovalMode mode, decimal displayTick, int height, bool header)
    {
        var viewModel = new PriceLadderViewModel(0.01m) { DisplayTickSize = displayTick };
        var levels = new List<PriceLevel>();
        for (var i = 0; i < 60; i++)
        {
            levels.Add(new PriceLevel(Side.BID, 100m - i * 0.01m, 10 + i, 1));
            levels.Add(new PriceLevel(Side.ASK, 100.01m + i * 0.01m, 10 + i, 1));
        }
        viewModel.ProcessBatch(levels);
        var snapshot = viewModel.CurrentSnapshot!.Value;

        var viewport = new ViewportManager
        {
            Width = Width, Height = height, RemovalMode = mode, ShowHeader = header, TickSize = 0.01m, DisplayTickSize = displayTick
        };
        using var renderer = new SkiaRenderer(new RenderConfig());
        using var surface = SKSurface.Create(new SKImageInfo(Width, height));
        renderer.Render(surface.Canvas, snapshot, viewport);

        var drawn = DrawnPrices(renderer);
        Assert.Equal(viewport.RowCount, drawn.Length);
        Assert.True(drawn.Count(price => price != null) > 10, "rows with levels");
        for (var row = 0; row < drawn.Length; row++)
        {
            Assert.Equal(drawn[row], viewport.LevelPriceAt(snapshot, viewport.RowTop(row)));
            Assert.Equal(drawn[row], viewport.LevelPriceAt(snapshot, viewport.RowTop(row) + RenderConfig.RowHeight - 0.5f));
        }
        if (header)
        {
            Assert.Null(viewport.LevelPriceAt(snapshot, viewport.HeaderHeight - 0.5f));
        }
    }

    // Each row's level as drawn (ask first, as a click finds it), null on an empty row
    private static decimal?[] DrawnPrices(SkiaRenderer renderer)
    {
        var rows = (Array)typeof(SkiaRenderer).GetField("_rows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(renderer)!;
        var rowType = rows.GetType().GetElementType()!;
        var (askField, bidField) = (rowType.GetField("Ask")!, rowType.GetField("Bid")!);
        var levelType = askField.FieldType;
        var (present, level) = (levelType.GetField("Present")!, levelType.GetField("Level")!);
        decimal? Price(object rowLevel) => (bool)present.GetValue(rowLevel)! ? ((BookLevel)level.GetValue(rowLevel)!).Price : null;
        return rows.Cast<object>().Select(row => Price(askField.GetValue(row)!) ?? Price(bidField.GetValue(row)!)).ToArray();
    }

    // A Work cell click raises OnWorkClick with its row's price, not OnLevelClick. In Show Empty
    // Rows a row without a level gives its own price, where working orders can sit
    [Theory]
    [InlineData(LevelRemovalMode.RemoveRow)]
    [InlineData(LevelRemovalMode.ShowEmpty)]
    public void WorkCellClick_RaisesOnWorkClickWithTheRowsPrice(LevelRemovalMode mode)
    {
        var viewModel = new PriceLadderViewModel(0.01m);
        viewModel.ProcessBatch(new[]
        {
            new PriceLevel(Side.BID, 100.00m, 10, 1), new PriceLevel(Side.BID, 99.99m, 10, 1),
            new PriceLevel(Side.ASK, 100.02m, 10, 1), new PriceLevel(Side.ASK, 100.03m, 10, 1)
        });
        var snapshot = viewModel.CurrentSnapshot!.Value;
        var viewport = new ViewportManager { Width = Width, Height = 600, RemovalMode = mode };
        viewport.Recenter(snapshot); // As a render with auto-center leaves it

        var work = new List<(decimal? Price, ClickButton Button)>();
        var levels = 0;
        viewModel.OnWorkClick += args => { work.Add((args.Price, args.Button)); args.Handled = true; };
        viewModel.OnLevelClick += _ => levels++;
        float RowY(decimal price) => viewport.RowTop(Enumerable.Range(0, viewport.RowCount)
            .Single(row => viewport.LevelPriceAt(snapshot, viewport.RowTop(row)) == price)) + 1;
        var workX = viewport.WorkColumnX!.Value + 1;

        Assert.True(viewModel.HandleClick(viewport, workX, RowY(100.00m), ClickButton.Left));
        Assert.True(viewModel.HandleClick(viewport, workX, RowY(100.02m), ClickButton.Right));
        Assert.False(viewModel.HandleClick(viewport, workX, 1, ClickButton.Left)); // Header
        // The row above the best bid: the spread (100.01) in Show Empty Rows, the best ask in Remove Rows
        viewModel.HandleClick(viewport, workX, RowY(100.00m) - RenderConfig.RowHeight, ClickButton.Left);
        Assert.Equal(new (decimal?, ClickButton)[]
        {
            (100.00m, ClickButton.Left), (100.02m, ClickButton.Right),
            (mode == LevelRemovalMode.ShowEmpty ? 100.01m : 100.02m, ClickButton.Left)
        }, work);
        Assert.Equal(0, levels);

        viewModel.HandleClick(viewport, viewport.BidQtyColumnX + 1, RowY(100.00m), ClickButton.Left);
        Assert.Equal(1, levels); // A qty cell is still a level click
    }

    // Bug: the demo cancelled a Work click's orders by exact price, but an aggregated row's price is
    // its bucket's, so a bid at 100.03 under the 100.0 row stayed working. DisplayPrice maps an
    // order to its row's price
    [Fact]
    public void CancelOwnOrders_FindsTheOrdersUnderAnAggregatedRow()
    {
        var viewModel = new PriceLadderViewModel(0.01m) { DisplayTickSize = 0.1m };
        using var simulator = new MarketDataSimulator(viewModel.Core);
        simulator.PlaceOwnOrder(100.03m, Side.BID);
        simulator.PlaceOwnOrder(100.03m, Side.ASK);
        Assert.Equal(100.0m, viewModel.DisplayPrice(100.03m, Side.BID)); // Bids round down
        Assert.Equal(100.1m, viewModel.DisplayPrice(100.03m, Side.ASK)); // Asks up

        simulator.CancelOwnOrders(100.0m, viewModel.DisplayPrice);
        Assert.Null(viewModel.Core.OrderBook.BidWork);
        Assert.NotNull(viewModel.Core.OrderBook.AskWork); // On the 100.1 row

        Assert.Equal(100.03m, new PriceLadderViewModel(0.01m).DisplayPrice(100.03m, Side.ASK)); // Not aggregating
    }

    // Any handler of the multicast event can mark a Work click handled
    [Fact]
    public void HandleWorkClick_ReturnsHandled()
    {
        var viewModel = new PriceLadderViewModel();
        Assert.False(viewModel.HandleWorkClick(100m, ClickButton.Middle)); // No handler
        viewModel.OnWorkClick += args => args.Handled = args.Button == ClickButton.Middle;
        viewModel.OnWorkClick += _ => { };
        Assert.True(viewModel.HandleWorkClick(100m, ClickButton.Middle));
        Assert.False(viewModel.HandleWorkClick(100m, ClickButton.Left));
    }

    [Theory]
    [InlineData(LevelRemovalMode.RemoveRow)]
    [InlineData(LevelRemovalMode.ShowEmpty)]
    public void Cells_DrawnOnTheirPriceRows(LevelRemovalMode mode)
    {
        var core = Core(out var snapshot);
        foreach (var (side, price) in new[] { (Side.BID, 100.00m), (Side.BID, 99.99m), (Side.ASK, 100.01m), (Side.ASK, 100.02m) })
        {
            core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(side, price, 10, 1));
        }
        core.Flush();
        core.SetWorkingOrders(100.00m, Side.BID, 10, 2);
        core.SetWorkingOrders(100.02m, Side.ASK, 5, 1);

        var config = new RenderConfig();
        using var renderer = new SkiaRenderer(config);
        using var surface = SKSurface.Create(new SKImageInfo(Width, 600));
        var viewport = new ViewportManager { Width = Width, Height = 600, RemovalMode = mode };
        int RowOf(decimal price) => Enumerable.Range(0, viewport.RowCount)
            .Single(row => viewport.LevelPriceAt(snapshot(), viewport.RowTop(row)) == price);
        SKColor LtqFill(decimal price)
        {
            renderer.Render(surface.Canvas, snapshot(), viewport);
            using var pixels = surface.PeekPixels();
            return pixels.GetPixelColor((int)viewport.LtqColumnX!.Value + 2, viewport.RowTop(RowOf(price)) + 2);
        }

        core.ProcessTrade(100.01m, 7, Side.BID);
        core.Flush();
        Assert.Equal(config.LtqBuyBackground, LtqFill(100.01m));
        core.ProcessTrade(100.01m, 7, Side.ASK);
        core.Flush();
        Assert.Equal(config.LtqSellBackground, LtqFill(100.01m));
        core.ProcessTrade(100.00m, 7);
        core.Flush();
        Assert.Equal(config.LtqUnknownBackground, LtqFill(100.00m));
        Assert.Equal(config.LtqBackground, LtqFill(100.01m)); // Only the last trade

        // Work text green on the buy row, red on the sell row
        using var work = surface.PeekPixels();
        (int Green, int Red) WorkTint(decimal price)
        {
            var (green, red) = (0, 0);
            var top = viewport.RowTop(RowOf(price));
            for (var y = top + 1; y < top + RenderConfig.RowHeight; y++)
            {
                for (var x = 0; x < (int)viewport.WorkColWidth; x++)
                {
                    var c = work.GetPixelColor(x, y);
                    green += c.Green > c.Red + 60 ? 1 : 0;
                    red += c.Red > c.Green + 60 ? 1 : 0;
                }
            }
            return (green, red);
        }
        var buy = WorkTint(100.00m);
        var sell = WorkTint(100.02m);
        Assert.True(buy.Green > 5 && buy.Red == 0, $"buy {buy}");
        Assert.True(sell.Red > 5 && sell.Green == 0, $"sell {sell}");
    }

    // The Orders column draws each order, so it shows for MBO books only (aggregated by display tick
    // too) and comes and goes with the mode
    [Fact]
    public void OrdersColumn_ShowsForMboBooksOnly()
    {
        var viewModel = new PriceLadderViewModel(0.01m);
        var levels = new[] { new PriceLevel(Side.BID, 100m, 10, 1), new PriceLevel(Side.ASK, 100.01m, 10, 1) };
        viewModel.ProcessBatch(levels);
        const int wide = 600; // Room for more than the bar column's minimum
        var viewport = new ViewportManager { Width = wide, Height = 600 };
        using var renderer = new SkiaRenderer(new RenderConfig());
        using var surface = SKSurface.Create(new SKImageInfo(wide, 600));
        void Render() => renderer.Render(surface.Canvas, viewModel.CurrentSnapshot!.Value, viewport);

        Render();
        Assert.Null(viewport.OrdersColumnX); // Price levels

        var core = viewModel.Core;
        core.SetDataMode(DataMode.MBO);
        core.ProcessOrderUpdate(new OrderUpdate(1, Side.BID, 100m, 10, 1), OrderUpdateType.Add);
        core.Flush();
        Render();
        Assert.Equal(viewport.LtqColumnX + viewport.LtqColWidth, viewport.OrdersColumnX);
        Assert.Equal(wide - viewport.OrdersColumnX!.Value, viewport.OrdersColumnWidth);

        viewModel.DisplayTickSize = 0.1m;
        core.ProcessOrderUpdate(new OrderUpdate(2, Side.ASK, 100.01m, 10, 2), OrderUpdateType.Add);
        core.Flush();
        Assert.NotNull(viewModel.CurrentSnapshot!.Value.BidOrders); // Aggregated: each bucket's orders
        Render();
        Assert.NotNull(viewport.OrdersColumnX);

        viewModel.DisplayTickSize = 0.01m;
        core.SetDataMode(DataMode.PriceLevel);
        viewModel.ProcessBatch(levels);
        Render();
        Assert.Null(viewport.OrdersColumnX);
    }

    // With the display tick aggregating, an MBO row shows the orders of every price level in its
    // bucket, the level nearest the spread first, with breaks where each next level starts. Buckets
    // whose levels didn't change keep their arrays, and the maps stay while no order changed
    [Fact]
    public void AggregatedMbo_MergesEveryLevelsOrders()
    {
        var viewModel = new PriceLadderViewModel(0.01m) { DisplayTickSize = 0.1m };
        var core = viewModel.Core;
        core.SetDataMode(DataMode.MBO);
        foreach (var (id, side, price) in new[]
        {
            (1L, Side.BID, 100.00m), (2L, Side.BID, 100.00m), (3L, Side.BID, 99.95m), (4L, Side.BID, 99.97m),
            (5L, Side.ASK, 100.04m), (6L, Side.ASK, 100.01m), (7L, Side.ASK, 100.01m)
        })
        {
            core.ProcessOrderUpdateNoFlush(new OrderUpdate(id, side, price, 10 * id, id), OrderUpdateType.Add);
        }
        core.Flush();
        var snapshot = viewModel.CurrentSnapshot!.Value;

        long[] Ids(Order[] orders) => orders.Select(order => order.OrderId).ToArray();
        Assert.Equal(new long[] { 1, 2 }, Ids(snapshot.BidOrders![100.0m]));
        Assert.Equal(new long[] { 4, 3 }, Ids(snapshot.BidOrders[99.9m])); // 99.97 before 99.95
        Assert.Equal(new[] { 1 }, snapshot.BidOrderBreaks![99.9m]);
        Assert.False(snapshot.BidOrderBreaks.ContainsKey(100.0m)); // One level
        Assert.Equal(new long[] { 6, 7, 5 }, Ids(snapshot.AskOrders![100.1m])); // 100.01 before 100.04
        Assert.Equal(new[] { 2 }, snapshot.AskOrderBreaks![100.1m]);

        var bucket = snapshot.BidOrders[99.9m];
        core.ProcessOrderUpdate(new OrderUpdate(1, Side.BID, 100.00m, 15, 1), OrderUpdateType.Modify);
        core.Flush();
        var next = viewModel.CurrentSnapshot!.Value;
        Assert.NotSame(snapshot.BidOrders, next.BidOrders);
        Assert.Same(bucket, next.BidOrders![99.9m]); // Its levels didn't change

        core.SetWorkingOrders(100m, Side.BID, 5, 1); // A snapshot with no order change
        Assert.Same(next.BidOrders, viewModel.CurrentSnapshot!.Value.BidOrders);
        Assert.Same(next.AskOrders, viewModel.CurrentSnapshot!.Value.AskOrders);
    }

    // Price levels in an aggregated MBO row are set apart by a wider gap than orders of one level
    [Fact]
    public void AggregatedMbo_LevelsAreSetApartInTheOrdersColumn()
    {
        var viewModel = new PriceLadderViewModel(0.01m) { DisplayTickSize = 0.1m };
        var core = viewModel.Core;
        core.SetDataMode(DataMode.MBO);
        // The 1000 lot sets the scale (200px), so 100 lots are 20px and the ask row fits
        foreach (var (id, side, price, quantity) in new[]
        {
            (1L, Side.BID, 100.00m, 1000m), (2L, Side.ASK, 100.01m, 100m), (3L, Side.ASK, 100.04m, 100m), (4L, Side.ASK, 100.04m, 100m)
        })
        {
            core.ProcessOrderUpdateNoFlush(new OrderUpdate(id, side, price, quantity, id), OrderUpdateType.Add);
        }
        core.Flush();
        var snapshot = viewModel.CurrentSnapshot!.Value;

        const int wide = 600;
        var config = new RenderConfig();
        var viewport = new ViewportManager { Width = wide, Height = 600, DisplayTickSize = 0.1m };
        using var renderer = new SkiaRenderer(config);
        using var surface = SKSurface.Create(new SKImageInfo(wide, 600));
        renderer.Render(surface.Canvas, snapshot, viewport);

        // Runs of the ask bar color along the top of the 100.1 row's bars: 100.01's order, then 100.04's two
        var row = Enumerable.Range(0, viewport.RowCount).Single(r => viewport.LevelPriceAt(snapshot, viewport.RowTop(r)) == 100.1m);
        using var pixels = surface.PeekPixels();
        var y = viewport.RowTop(row) + 5;
        var runs = new List<(int Start, int End)>();
        for (var x = (int)viewport.OrdersColumnX!.Value; x < wide; x++)
        {
            if (pixels.GetPixelColor(x, y) != config.AskVolumeBar)
            {
                continue;
            }
            if (runs.Count > 0 && runs[^1].End == x - 1)
            {
                runs[^1] = (runs[^1].Start, x);
            }
            else
            {
                runs.Add((x, x));
            }
        }
        Assert.Equal(3, runs.Count);
        Assert.InRange(runs[1].Start - runs[0].End - 1, RenderConfig.OrderLevelGapPx - 1, RenderConfig.OrderLevelGapPx + 1);
        Assert.InRange(runs[2].Start - runs[1].End - 1, RenderConfig.SegmentGapPx - 1, RenderConfig.SegmentGapPx + 1);
    }

    [Fact]
    public void Header_SitsAboveTheRows()
    {
        var config = new RenderConfig();
        var core = Core(out var snapshot);
        core.ProcessPriceLevelUpdate(new PriceLevel(Side.BID, 100m, 10, 1));
        core.Flush();

        // Header, the line under it, the separator on the price column's left edge and next to it,
        // then the separators on both sides
        SKColor[] Pixels(bool header)
        {
            using var renderer = new SkiaRenderer(config);
            using var surface = SKSurface.Create(new SKImageInfo(Width, 600));
            var viewport = new ViewportManager { Width = Width, Height = 600, ShowHeader = header };
            renderer.Render(surface.Canvas, snapshot(), viewport);
            using var pixels = surface.PeekPixels();
            var separator = (int)MathF.Floor(viewport.PriceColumnX);
            return new[]
            {
                pixels.GetPixelColor(1, 1), pixels.GetPixelColor(1, RenderConfig.RowHeight),
                pixels.GetPixelColor(separator, 1), pixels.GetPixelColor(separator - 2, 1),
                pixels.GetPixelColor(0, 1), pixels.GetPixelColor(Width - 1, 1)
            };
        }

        Assert.Equal(new[]
        {
            config.HeaderBackground, config.GridLineColor, config.GridLineColor, config.HeaderBackground,
            config.GridLineColor, config.GridLineColor
        }, Pixels(header: true));
        Assert.NotEqual(config.HeaderBackground, Pixels(header: false)[0]);
    }
}
