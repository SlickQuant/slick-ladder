using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using SkiaSharp;
using SlickLadder.Core.Models;
using SlickLadder.Rendering.Core;
using SlickLadder.Rendering.ViewModels;
using Xunit;

namespace SlickLadder.Core.Tests;

/// <summary>
/// SkiaRenderer redraws only the rows that changed, and moves rows that only moved. Each test
/// renders a sequence that way, then compares with a fresh renderer drawing the last snapshot
/// from scratch
/// </summary>
public class IncrementalRenderTests
{
    private const int Width = 400;
    private const int Height = 600;

    // (removal mode, retained target)
    public static TheoryData<LevelRemovalMode, bool> Paths => new()
    {
        { LevelRemovalMode.RemoveRow, false },
        { LevelRemovalMode.RemoveRow, true },
        { LevelRemovalMode.ShowEmpty, false },
        { LevelRemovalMode.ShowEmpty, true },
    };

    /// <summary>
    /// A book fed through PriceLadderCore, so snapshots come out as they do in the app
    /// </summary>
    private sealed class Book
    {
        private readonly PriceLadderCore _core = new();

        public OrderBookSnapshot Snapshot { get; private set; }

        // snapshotLevels: ticks around the mid a snapshot spans
        public Book(DataMode mode = DataMode.PriceLevel, int snapshotLevels = 100)
        {
            _core.SetDataMode(mode);
            _core.SetSnapshotViewport(null, snapshotLevels);
            // Marked MBO, so the Orders column shows. A price level book then draws like an MBO
            // snapshot without orders, one bar per level
            _core.OnSnapshotReady += snapshot => Snapshot = snapshot with { Mode = DataMode.MBO };
        }

        /// <summary>Bids mid-0.10..mid and asks mid+0.01..mid+0.11, 1000 each</summary>
        public static Book Seeded(decimal mid = 100m)
        {
            var book = new Book();
            for (var i = 0; i <= 10; i++)
            {
                book._core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(Side.BID, mid - i * 0.01m, 1000, 5));
                book._core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(Side.ASK, mid + (i + 1) * 0.01m, 1000, 5));
            }
            book._core.Flush();
            return book;
        }

        public Book Flush(params PriceLevel[] updates)
        {
            foreach (var update in updates) _core.ProcessPriceLevelUpdateNoFlush(update);
            _core.Flush();
            return this;
        }

        public Book FlushOrders(OrderUpdateType type, params OrderUpdate[] updates)
        {
            foreach (var update in updates) _core.ProcessOrderUpdateNoFlush(update, type);
            _core.Flush();
            return this;
        }

        public void SetHasOwnOrders(decimal price, Side side) => _core.SetHasOwnOrders(price, side, true);

        public void SetWorkingOrders(decimal price, Side side, decimal quantity, int count) =>
            _core.SetWorkingOrders(price, side, quantity, count);

        public Book Trade(decimal price, decimal quantity, Side? aggressor)
        {
            _core.ProcessTrade(price, quantity, aggressor);
            _core.Flush();
            return this;
        }
    }

    /// <summary>
    /// The renderer under test, its viewport and the surface it draws into (raster, or GPU when
    /// given a context)
    /// </summary>
    private sealed class Ladder : IDisposable
    {
        private readonly LevelRemovalMode _mode;
        private readonly bool _retained;
        private readonly bool _showOrderCount;
        private readonly bool _cells;
        private readonly decimal _displayTickSize;
        private readonly int _width;
        private readonly GRContext? _gpu;

        public SkiaRenderer Renderer { get; } = new(new RenderConfig());
        public ViewportManager Viewport { get; }
        public SKSurface Surface { get; }

        // cells: the Work and LTQ columns and the header, all on or all off (the layout before them)
        public Ladder(LevelRemovalMode mode, bool retained, bool showOrderCount = false, int width = Width, GRContext? gpu = null,
            bool cells = true, decimal displayTickSize = 0.01m)
        {
            (_mode, _retained, _showOrderCount, _width, _gpu, _cells) = (mode, retained, showOrderCount, width, gpu, cells);
            _displayTickSize = displayTickSize;
            Surface = NewSurface();
            Viewport = NewViewport();
        }

        private SKSurface NewSurface() => _gpu == null
            ? SKSurface.Create(new SKImageInfo(_width, Height))
            : SKSurface.Create(_gpu, false, new SKImageInfo(_width, Height));

        // Same settings, untouched by rendering (Render recenters and sizes columns)
        public ViewportManager NewViewport() => new()
        {
            Width = _width, Height = Height, RemovalMode = _mode, ShowOrderCount = _showOrderCount,
            ShowWorkColumn = _cells, ShowLtqColumn = _cells, ShowHeader = _cells, DisplayTickSize = _displayTickSize
        };

        public void Render(in OrderBookSnapshot snapshot)
        {
            if (_retained)
            {
                Renderer.RenderRetained(Surface, snapshot, Viewport);
            }
            else
            {
                Surface.Canvas.Clear(SKColors.Magenta); // Render must cover every pixel
                Renderer.Render(Surface.Canvas, snapshot, Viewport);
            }
        }

        public void AssertMatchesFullRedraw(in OrderBookSnapshot snapshot, string context = "")
        {
            using var renderer = new SkiaRenderer(new RenderConfig());
            using var expected = NewSurface();
            renderer.Render(expected.Canvas, snapshot, NewViewport());

            var want = Pixels(expected);
            var got = Pixels(Surface);
            var index = want.CommonPrefixLength(got);
            Assert.True(index == want.Length,
                $"{context} pixel ({index / 4 % _width}, {index / 4 / _width}) differs from a full redraw");
        }

        /// <summary>
        /// Same pixels as a ladder that redrew everything for the same frame. Unlike
        /// AssertMatchesFullRedraw, that ladder has seen every earlier frame too, so history-bound
        /// state such as the segment scale (which moves only past 20%) is the same
        /// </summary>
        public void AssertMatches(Ladder fullRedraw, string context)
        {
            var want = fullRedraw.Pixels(fullRedraw.Surface);
            var got = Pixels(Surface);
            var index = want.CommonPrefixLength(got);
            Assert.True(index == want.Length,
                $"{context} pixel ({index / 4 % _width}, {index / 4 / _width}) differs from a full redraw");
        }

        // A raster surface's pixels in place, a GPU surface's read back
        private ReadOnlySpan<byte> Pixels(SKSurface surface)
        {
            if (_gpu == null)
            {
                return surface.PeekPixels().GetPixelSpan();
            }
            using var bitmap = new SKBitmap(new SKImageInfo(_width, Height));
            Assert.True(surface.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0), "GPU read back failed");
            return bitmap.Bytes;
        }

        public void Dispose()
        {
            Renderer.Dispose();
            Surface.Dispose();
        }
    }

    // Bug: each flush's snapshot lists only its own changed rows, so with two flushes between
    // frames the first flush's row was never redrawn
    [Theory]
    [MemberData(nameof(Paths))]
    public void TwoFlushesBetweenFrames_BothChangesShow(LevelRemovalMode mode, bool retained)
    {
        var book = Book.Seeded();
        using var ladder = new Ladder(mode, retained);
        ladder.Render(book.Snapshot);

        book.Flush(new PriceLevel(Side.ASK, 100.03m, 555, 3));
        book.Flush(new PriceLevel(Side.BID, 99.97m, 777, 4));
        ladder.Render(book.Snapshot);

        ladder.AssertMatchesFullRedraw(book.Snapshot);
    }

    // Bug: a new largest quantity rescales every volume bar, but only the changed row was redrawn
    [Theory]
    [MemberData(nameof(Paths))]
    public void VolumeScaleChange_RedrawsEveryBar(LevelRemovalMode mode, bool retained)
    {
        var book = Book.Seeded();
        using var ladder = new Ladder(mode, retained);
        ladder.Render(book.Snapshot);

        book.Flush(new PriceLevel(Side.ASK, 100.02m, 5000, 9));
        ladder.Render(book.Snapshot);
        ladder.AssertMatchesFullRedraw(book.Snapshot);

        book.Flush(new PriceLevel(Side.ASK, 100.02m, 1000, 5)); // Back down: bars grow again
        ladder.Render(book.Snapshot);
        ladder.AssertMatchesFullRedraw(book.Snapshot);
    }

    // Bug: on a ladder too narrow to show the bar column, a bars-only row redraw copied the
    // template from past the right edge and threw
    [Theory]
    [MemberData(nameof(Paths))]
    public void VolumeScaleChange_NarrowLadder_DoesNotThrow(LevelRemovalMode mode, bool retained)
    {
        var book = Book.Seeded();
        book.SetHasOwnOrders(100.03m, Side.ASK); // Remove Rows replays this row clipped to the bar column
        using var ladder = new Ladder(mode, retained, width: 100);
        ladder.Render(book.Snapshot);
        Assert.True(ladder.Viewport.OrdersColumnX >= 100, "the bar column must start past the right edge");

        book.Flush(new PriceLevel(Side.ASK, 100.02m, 2000, 9)); // Same digits: only the bar scale moves
        ladder.Render(book.Snapshot);

        ladder.AssertMatchesFullRedraw(book.Snapshot);
    }

    // Without the LTQ column the ask qty column sits next to the bars, so a bars-only redraw of an
    // ask own-order row must keep the border's pixel in the bar column
    [Theory]
    [MemberData(nameof(Paths))]
    public void VolumeScaleChange_AskOwnOrderWithOrderCount_MatchesFullRedraw(LevelRemovalMode mode, bool retained)
    {
        var book = Book.Seeded();
        book.SetHasOwnOrders(100.03m, Side.ASK);
        using var ladder = new Ladder(mode, retained, showOrderCount: true, cells: false);
        ladder.Render(book.Snapshot);
        var qtyColWidth = ladder.Viewport.QtyColWidth;

        book.Flush(new PriceLevel(Side.ASK, 100.02m, 5000, 9)); // Same text widths: only the bar scale moves
        ladder.Render(book.Snapshot);

        Assert.Equal(qtyColWidth, ladder.Viewport.QtyColWidth);
        ladder.AssertMatchesFullRedraw(book.Snapshot);
    }

    // The LTQ column sits next to the bars: a bars-only redraw of the traded row must keep the LTQ
    // fill's edge pixel
    [Theory]
    [MemberData(nameof(Paths))]
    public void VolumeScaleChange_LtqNextToBars_MatchesFullRedraw(LevelRemovalMode mode, bool retained)
    {
        var book = Book.Seeded().Trade(100.03m, 1234, Side.BID);
        using var ladder = new Ladder(mode, retained);
        ladder.Render(book.Snapshot);
        var ltqColWidth = ladder.Viewport.LtqColWidth;

        book.Flush(new PriceLevel(Side.ASK, 100.02m, 5000, 9)); // Only the bar scale moves
        ladder.Render(book.Snapshot);

        Assert.Equal(ltqColWidth, ladder.Viewport.LtqColWidth);
        Assert.Equal(1, CellRows(ladder.Renderer).Ltq);
        ladder.AssertMatchesFullRedraw(book.Snapshot);
    }

    // Working orders and trades come and go and resize their columns. The header stays put while
    // Show Empty Rows shifts the rows under it
    [Theory]
    [MemberData(nameof(Paths))]
    public void WorkAndTrades_MatchFullRedraw(LevelRemovalMode mode, bool retained)
    {
        var book = Book.Seeded();
        using var ladder = new Ladder(mode, retained);
        ladder.Render(book.Snapshot);

        book.SetWorkingOrders(100.00m, Side.BID, 10, 2);
        book.SetWorkingOrders(100.02m, Side.ASK, 5, 1);
        ladder.Render(book.Snapshot);
        Assert.Equal(2, CellRows(ladder.Renderer).Work);
        ladder.AssertMatchesFullRedraw(book.Snapshot, "work:");

        book.Trade(100.01m, 7, Side.BID);
        ladder.Render(book.Snapshot);
        ladder.AssertMatchesFullRedraw(book.Snapshot, "trade:");

        book.SetWorkingOrders(100.00m, Side.BID, 123456789, 12); // Widens the Work column
        book.Trade(100.00m, 98765, null);
        ladder.Render(book.Snapshot);
        ladder.AssertMatchesFullRedraw(book.Snapshot, "wider:");

        book.Flush(new PriceLevel(Side.ASK, 100.01m, 0, 0)); // Rows move (Remove Rows) or shift (Show Empty)
        ladder.Render(book.Snapshot);
        ladder.AssertMatchesFullRedraw(book.Snapshot, "moved:");

        book.SetWorkingOrders(100.00m, Side.BID, 0, 0);
        ladder.Render(book.Snapshot);
        ladder.AssertMatchesFullRedraw(book.Snapshot, "cleared:");
    }

    // With order count on, the count sits on the outer side of each qty column
    [Theory]
    [InlineData(Side.BID, true, "(5) 102")]
    [InlineData(Side.ASK, true, "102 (5)")]
    [InlineData(Side.BID, false, "102")]
    [InlineData(Side.ASK, false, "102")]
    public void QuantityText_PutsTheOrderCountOutside(Side side, bool showOrderCount, string expected)
    {
        using var renderer = new SkiaRenderer(new RenderConfig());
        var text = typeof(SkiaRenderer).GetMethod("QuantityText", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(renderer, new object[] { 102m, 5, side, showOrderCount });
        Assert.Equal(expected, text);
    }

    // The qty column widens to fit the order count, more for a longer count, and not at all when it's hidden
    [Fact]
    public void OrderCount_WidensTheQtyColumnToFit()
    {
        float QtyColWidth(Book book, bool showOrderCount)
        {
            using var ladder = new Ladder(LevelRemovalMode.RemoveRow, retained: false, showOrderCount);
            ladder.Render(book.Snapshot);
            return ladder.Viewport.QtyColWidth;
        }

        var book = Book.Seeded();
        var plain = QtyColWidth(book, false);
        var counted = QtyColWidth(book, true);
        Assert.True(counted > plain, $"{counted} not wider than {plain}");

        book.Flush(new PriceLevel(Side.BID, 99.99m, 1000, 12345));
        Assert.True(QtyColWidth(book, true) > counted, "a longer count widens it");
        Assert.Equal(plain, QtyColWidth(book, false));
    }

    // Bug: Render presented its cache through one SKImage wrapping the cache's pixels. A GPU
    // canvas uploads an image once and keeps that texture, so it showed the first frame forever
    [Theory]
    [MemberData(nameof(Paths))]
    public void GpuTarget_ShowsEveryFrame(LevelRemovalMode mode, bool retained) => OnGpu(gpu =>
    {
        var book = Book.Seeded().Trade(100.03m, 1234, Side.ASK); // The LTQ fill sits next to the bars
        book.SetWorkingOrders(100.00m, Side.BID, 10, 2);
        using var ladder = new Ladder(mode, retained, gpu: gpu);
        ladder.Render(book.Snapshot);

        book.Flush(new PriceLevel(Side.ASK, 100.02m, 2000, 9)); // Bars rescale
        ladder.Render(book.Snapshot);
        ladder.AssertMatchesFullRedraw(book.Snapshot, "bars:");

        book.Flush(new PriceLevel(Side.ASK, 100.01m, 0, 0)); // Rows move (Remove Rows) or shift (Show Empty)
        ladder.Render(book.Snapshot);
        ladder.AssertMatchesFullRedraw(book.Snapshot, "moved:");
    });

    // A GPU cache may only be freed on the thread that renders with its context. Disposing the
    // renderer anywhere else leaves it for the next Render on that context
    [Fact]
    public void GpuCache_FreedOnItsRenderThread() => OnGpu(gpu =>
    {
        var book = Book.Seeded();
        using var target = SKSurface.Create(gpu, false, new SKImageInfo(Width, Height));
        SKSurface Cache(SkiaRenderer renderer) => RenderedCache(renderer, target, book.Snapshot);

        var elsewhere = new SkiaRenderer(new RenderConfig());
        var cache = Cache(elsewhere);
        // A thread of its own: Task.Wait may run a task that hasn't started on the waiting thread
        var thread = new Thread(elsewhere.Dispose);
        thread.Start();
        thread.Join();
        Assert.NotEqual(IntPtr.Zero, cache.Handle); // Not freed off its thread

        using var next = new SkiaRenderer(new RenderConfig());
        Cache(next);
        Assert.Equal(IntPtr.Zero, cache.Handle); // Freed by the next render on its GPU

        var here = new SkiaRenderer(new RenderConfig());
        var hereCache = Cache(here);
        here.Dispose();
        Assert.Equal(IntPtr.Zero, hereCache.Handle);
    });

    // Bug: a renderer never disposed kept its GPU cache forever, since the cache's finalizer is
    // suppressed. Its own finalizer now leaves the cache for the next render on that GPU
    [Fact]
    public void GpuCache_OfADroppedRenderer_FreedByTheNextRender() => OnGpu(gpu =>
    {
        var book = Book.Seeded();
        using var target = SKSurface.Create(gpu, false, new SKImageInfo(Width, Height));
        var cache = CacheOfDroppedRenderer(target, book.Snapshot);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.NotEqual(IntPtr.Zero, cache.Handle); // Not freed on the finalizer thread

        using var next = new SkiaRenderer(new RenderConfig());
        RenderedCache(next, target, book.Snapshot);
        Assert.Equal(IntPtr.Zero, cache.Handle);
    });

    // Not inlined, so nothing in the caller keeps the renderer reachable
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SKSurface CacheOfDroppedRenderer(SKSurface target, OrderBookSnapshot snapshot) =>
        RenderedCache(new SkiaRenderer(new RenderConfig()), target, snapshot);

    // The cache a renderer draws into after rendering one frame to target
    private static SKSurface RenderedCache(SkiaRenderer renderer, SKSurface target, OrderBookSnapshot snapshot)
    {
        renderer.Render(target.Canvas, snapshot, new ViewportManager { Width = Width, Height = Height });
        return (SKSurface)typeof(SkiaRenderer).GetField("_cachedSurface", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(renderer)!;
    }

    // Runs test on an OpenGL context. CI runners have none: the test returns without running
    private static void OnGpu(Action<GRContext> test)
    {
        using var gl = GlContext.TryCreate();
        if (gl != null)
        {
            test(gl.Gpu);
        }
    }

    // Bug: Remove Rows drew prices with two decimals, centered on the default column width. Show
    // Empty Rows (and web) use the display tick's decimals, centered on the price column
    [Fact]
    public void RemoveRowPrice_DrawnLikeShowEmptyLabel()
    {
        var core = new PriceLadderCore(tickSize: 0.001m);
        OrderBookSnapshot snapshot = default;
        core.OnSnapshotReady += s => snapshot = s;
        core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(Side.BID, 123456.122m, 10, 1));
        core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(Side.ASK, 123456.123m, 10, 1));
        core.Flush();

        // The ask is on row 11 in Remove Rows (row 12 holds the bid), and on the middle row 12 in
        // Show Empty Rows (the 123456.1225 mid rounds up)
        var (removeRow, removeViewport) = Draw(LevelRemovalMode.RemoveRow);
        var (showEmpty, showEmptyViewport) = Draw(LevelRemovalMode.ShowEmpty);
        Assert.True(removeViewport.PriceColWidth > RenderConfig.ColumnWidth, "the price must widen its column");
        var left = (int)MathF.Ceiling(removeViewport.PriceColumnX) + 1;
        var right = (int)(removeViewport.PriceColumnX + removeViewport.PriceColWidth) - 1;
        for (var y = 1; y < RenderConfig.RowHeight; y++)
        {
            for (var x = left; x < right; x++)
            {
                Assert.True(removeRow.GetPixel(x, removeViewport.RowTop(11) + y) == showEmpty.GetPixel(x, showEmptyViewport.RowTop(12) + y),
                    $"price cell pixel ({x}, {y}) differs");
            }
        }

        (SKBitmap, ViewportManager) Draw(LevelRemovalMode mode)
        {
            var viewport = new ViewportManager { Width = Width, Height = Height, RemovalMode = mode, TickSize = 0.001m, DisplayTickSize = 0.001m };
            using var renderer = new SkiaRenderer(new RenderConfig());
            var bitmap = new SKBitmap(new SKImageInfo(Width, Height));
            using var canvas = new SKCanvas(bitmap);
            renderer.Render(canvas, snapshot, viewport);
            return (bitmap, viewport);
        }
    }

    // Price and quantity texts are cached by value: a new display tick or quantity threshold
    // must not draw the old texts
    [Theory]
    [MemberData(nameof(Paths))]
    public void TextFormatChange_DrawsTheNewTexts(LevelRemovalMode mode, bool retained)
    {
        var book = Book.Seeded();
        var config = new RenderConfig();
        using var renderer = new SkiaRenderer(config);
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        var viewport = Viewport(0.01m);
        Render(renderer, viewport);

        viewport.DisplayTickSize = 0.1m; // "100.00" -> "100.0"
        Render(renderer, viewport);
        AssertMatchesFresh(new RenderConfig(), 0.1m, "tick:");

        config.MinQuantityThreshold = 5000m; // "1,000" -> "<5000"
        Render(renderer, viewport);
        AssertMatchesFresh(new RenderConfig { MinQuantityThreshold = 5000m }, 0.1m, "threshold:");

        ViewportManager Viewport(decimal displayTickSize) =>
            new() { Width = Width, Height = Height, RemovalMode = mode, DisplayTickSize = displayTickSize };

        void Render(SkiaRenderer target, ViewportManager view)
        {
            if (retained)
            {
                target.RenderRetained(surface, book.Snapshot, view);
            }
            else
            {
                target.Render(surface.Canvas, book.Snapshot, view);
            }
        }

        void AssertMatchesFresh(RenderConfig freshConfig, decimal displayTickSize, string context)
        {
            using var fresh = new SkiaRenderer(freshConfig);
            using var expected = SKSurface.Create(new SKImageInfo(Width, Height));
            fresh.Render(expected.Canvas, book.Snapshot, Viewport(displayTickSize));
            Assert.True(expected.PeekPixels().GetPixelSpan().SequenceEqual(surface.PeekPixels().GetPixelSpan()),
                $"{context} differs from a fresh renderer");
        }
    }

    // Bug: a Show Empty Rows row redrawn alone centered its price on the default column width,
    // not on the (wider) price column a full redraw uses
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShowEmptyRowRedraw_KeepsPriceCentered(bool retained)
    {
        var book = Book.Seeded(123456.00m);
        using var ladder = new Ladder(LevelRemovalMode.ShowEmpty, retained);
        ladder.Render(book.Snapshot);
        Assert.True(ladder.Viewport.PriceColWidth > RenderConfig.ColumnWidth, "prices must widen the price column");

        book.Flush(new PriceLevel(Side.BID, 123455.95m, 500, 2));
        ladder.Render(book.Snapshot);

        ladder.AssertMatchesFullRedraw(book.Snapshot);
    }

    // Bug: the price column fit the highest price anywhere in the book, so a full exchange book
    // (Coinbase BTC-USD has asks out at 138,991,023.41) made it wide enough for prices never on
    // screen. Now it fits the prices on screen and changes as the view scrolls or re-centers.
    // PriceLadderCore's snapshots only hold levels near the mid, so this test raises
    // snapshotLevels to reach the far ask
    [Theory]
    [MemberData(nameof(Paths))]
    public void PriceColumn_FitsPricesOnScreen(LevelRemovalMode mode, bool retained)
    {
        const decimal farAsk = 1_000_000.00m;
        const int snapshotLevels = 200_000_000;
        var levels = new List<PriceLevel>();
        for (var i = 0; i < 30; i++)
        {
            levels.Add(new PriceLevel(Side.BID, 64964.81m - i * 0.01m, 1, 1));
            levels.Add(new PriceLevel(Side.ASK, 64964.82m + i * 0.01m, 1, 1));
        }
        using var nearOnly = new Ladder(mode, retained);
        nearOnly.Render(new Book(snapshotLevels: snapshotLevels).Flush(levels.ToArray()).Snapshot);
        var near = nearOnly.Viewport.PriceColWidth;

        levels.Add(new PriceLevel(Side.ASK, farAsk, 1, 1));
        var book = new Book(snapshotLevels: snapshotLevels).Flush(levels.ToArray());
        Assert.Contains(book.Snapshot.Asks, level => level.Price == farAsk);
        using var ladder = new Ladder(mode, retained);
        ladder.Render(book.Snapshot);
        Assert.Equal(near, ladder.Viewport.PriceColWidth);

        // Scrolled to the far ask, the column fits it. Re-centering narrows it again
        ladder.Viewport.AutoCenter = false;
        if (mode == LevelRemovalMode.RemoveRow)
        {
            ladder.Viewport.DensePackingScrollOffset = -30;
        }
        else
        {
            ladder.Viewport.CenterPrice = farAsk;
        }
        ladder.Render(book.Snapshot);
        Assert.True(ladder.Viewport.PriceColWidth > near, "the far ask must widen the price column");

        ladder.Viewport.AutoCenter = true;
        ladder.Render(book.Snapshot);
        Assert.Equal(near, ladder.Viewport.PriceColWidth);
        ladder.AssertMatchesFullRedraw(book.Snapshot);
    }

    // Bug: only the highest price was measured, starting from 0, so a book entirely below zero
    // (a calendar spread) kept the default width and clipped its prices
    [Theory]
    [MemberData(nameof(Paths))]
    public void PriceColumn_FitsNegativePrices(LevelRemovalMode mode, bool retained)
    {
        using var positive = new Ladder(mode, retained);
        positive.Render(new Book().Flush(new PriceLevel(Side.BID, 123456.50m, 10, 1), new PriceLevel(Side.ASK, 123456.75m, 10, 1)).Snapshot);
        Assert.True(positive.Viewport.PriceColWidth > RenderConfig.ColumnWidth, "the digits must widen the price column");

        var book = new Book().Flush(new PriceLevel(Side.BID, -123456.75m, 10, 1), new PriceLevel(Side.ASK, -123456.50m, 10, 1));
        using var ladder = new Ladder(mode, retained);
        ladder.Render(book.Snapshot);
        Assert.True(ladder.Viewport.PriceColWidth > positive.Viewport.PriceColWidth, "the minus sign must widen it more");
        ladder.AssertMatchesFullRedraw(book.Snapshot);
    }

    // A redrawn row also drew the grid line below it, cutting into the next row's own-order border
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RowRedraw_KeepsOwnOrderBorderBelow(bool retained)
    {
        var book = Book.Seeded();
        book.SetHasOwnOrders(100.03m, Side.ASK);
        using var ladder = new Ladder(LevelRemovalMode.RemoveRow, retained);
        ladder.Render(book.Snapshot);

        book.Flush(new PriceLevel(Side.ASK, 100.04m, 500, 2)); // The row just above 100.03
        ladder.Render(book.Snapshot);

        ladder.AssertMatchesFullRedraw(book.Snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CenterMove_ShowEmpty_MatchesFullRedraw(bool retained)
    {
        var book = Book.Seeded();
        using var ladder = new Ladder(LevelRemovalMode.ShowEmpty, retained);
        ladder.Render(book.Snapshot);

        // Best ask 100.01 -> 100.06: the mid moves up and the rows shift down
        book.Flush(
            new PriceLevel(Side.ASK, 100.01m, 0, 0), new PriceLevel(Side.ASK, 100.02m, 0, 0),
            new PriceLevel(Side.ASK, 100.03m, 0, 0), new PriceLevel(Side.ASK, 100.04m, 0, 0),
            new PriceLevel(Side.ASK, 100.05m, 0, 0));
        ladder.Render(book.Snapshot);
        ladder.AssertMatchesFullRedraw(book.Snapshot, "up:");

        book.Flush(new PriceLevel(Side.ASK, 100.01m, 800, 1)); // And back down
        ladder.Render(book.Snapshot);
        ladder.AssertMatchesFullRedraw(book.Snapshot, "down:");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LevelRemovedAndAdded_RemoveRow_MatchesFullRedraw(bool retained)
    {
        var book = Book.Seeded();
        using var ladder = new Ladder(LevelRemovalMode.RemoveRow, retained);
        ladder.Render(book.Snapshot);

        // Deeper asks move down a row, then back up
        book.Flush(new PriceLevel(Side.ASK, 100.03m, 0, 0));
        ladder.Render(book.Snapshot);
        ladder.AssertMatchesFullRedraw(book.Snapshot, "removed:");

        book.Flush(new PriceLevel(Side.ASK, 100.03m, 900, 2), new PriceLevel(Side.BID, 99.98m, 0, 0));
        ladder.Render(book.Snapshot);
        ladder.AssertMatchesFullRedraw(book.Snapshot, "added:");
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void MboOrderChange_MatchesFullRedraw(LevelRemovalMode mode, bool retained) => MboOrderChange(mode, retained, gpu: null);

    [Theory]
    [MemberData(nameof(Paths))]
    public void MboOrderChange_GpuTarget_MatchesFullRedraw(LevelRemovalMode mode, bool retained) =>
        OnGpu(gpu => MboOrderChange(mode, retained, gpu));

    private static void MboOrderChange(LevelRemovalMode mode, bool retained, GRContext? gpu)
    {
        var book = new Book(DataMode.MBO);
        var id = 0L;
        for (var i = 0; i < 6; i++)
        {
            book.FlushOrders(OrderUpdateType.Add,
                new OrderUpdate(++id, Side.BID, 100.00m - i * 0.01m, 100 + i * 50, id),
                new OrderUpdate(++id, Side.BID, 100.00m - i * 0.01m, 300, id, isOwnOrder: i == 2),
                new OrderUpdate(++id, Side.ASK, 100.01m + i * 0.01m, 200 + i * 40, id));
        }
        using var ladder = new Ladder(mode, retained, gpu: gpu);
        ladder.Render(book.Snapshot);

        book.FlushOrders(OrderUpdateType.Modify, new OrderUpdate(5, Side.BID, 99.99m, 900, 5));
        book.FlushOrders(OrderUpdateType.Delete, new OrderUpdate(9, Side.ASK, 100.03m, 0, 9));
        ladder.Render(book.Snapshot);

        ladder.AssertMatchesFullRedraw(book.Snapshot);
    }

    // Random feed, several flushes per frame, both column layouts: every frame matches a full redraw
    [Theory]
    [MemberData(nameof(Paths))]
    public void RandomFeed_EveryFrameMatchesFullRedraw(LevelRemovalMode mode, bool retained) => RandomFeed(mode, retained, gpu: null);

    // Same on a GPU surface, where rows are moved, shifted and blanked by drawing, not by copying pixels
    [Theory]
    [MemberData(nameof(Paths))]
    public void RandomFeed_GpuTarget_EveryFrameMatchesFullRedraw(LevelRemovalMode mode, bool retained) =>
        OnGpu(gpu => RandomFeed(mode, retained, gpu));

    private static void RandomFeed(LevelRemovalMode mode, bool retained, GRContext? gpu)
    {
        // Work, LTQ and header on, then the layout without them
        foreach (var (showOrderCount, cells) in new[] { (false, true), (true, false) })
        {
            var feed = new RandomBookFeed(seed: 7, ticksPerSecond: 150);
            var viewModel = new PriceLadderViewModel(RandomBookFeed.TickSize);
            viewModel.ProcessBatch(feed.Seed());
            using var ladder = new Ladder(mode, retained, showOrderCount, gpu: gpu, cells: cells);
            var (workFrames, ltqFrames) = (0, 0);

            for (var frame = 0; frame < 120; frame++)
            {
                WorkAndTrades(viewModel.Core, feed, frame);
                foreach (var batch in feed.Advance((frame + 1) * 1000.0 / 60)) viewModel.ProcessBatch(batch);
                var snapshot = viewModel.CurrentSnapshot!.Value;
                if (ladder.Renderer.NeedsRender(snapshot, ladder.Viewport))
                {
                    ladder.Render(snapshot);
                }
                ladder.AssertMatchesFullRedraw(snapshot, $"order count {showOrderCount}, cells {cells}, frame {frame}:");
                var (work, ltq) = CellRows(ladder.Renderer);
                workFrames += work > 0 ? 1 : 0;
                ltqFrames += ltq > 0 ? 1 : 0;
            }
            Assert.True(!cells || (workFrames > 30 && ltqFrames > 30), $"work on {workFrames} frames, LTQ on {ltqFrames}");
        }
    }

    // Working orders and a trade near the touch, so Work and LTQ cells come and go, move rows and
    // resize their columns (mirrors workAndTrades in incremental-render.test.js)
    private static void WorkAndTrades(PriceLadderCore core, RandomBookFeed feed, int frame)
    {
        var tick = RandomBookFeed.TickSize;
        if (frame % 5 == 0 && feed.BestBid is { } bid && feed.BestAsk is { } ask)
        {
            var side = frame % 2 == 1 ? Side.BID : Side.ASK;
            var price = side == Side.BID ? bid - (frame % 4) * tick : ask + (frame % 4) * tick;
            var quantity = frame % 15 == 0 ? 0 : (decimal)Math.Pow(10, frame % 6);
            core.SetWorkingOrders(price, side, quantity, 1 + frame % 3);
        }
        if (frame % 3 == 0 && feed.BestAsk is { } best)
        {
            Side? aggressor = (frame % 9 / 3) switch { 0 => Side.BID, 1 => Side.ASK, _ => null };
            core.ProcessTrade(best + (frame % 7 - 3) * tick, 1 + frame * 37 % 2000, aggressor);
        }
    }

    // Rows of the last render with a Work cell and with an LTQ cell, read from the renderer's row states
    private static (int Work, int Ltq) CellRows(SkiaRenderer renderer)
    {
        var rows = (Array)typeof(SkiaRenderer).GetField("_rows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(renderer)!;
        var rowType = rows.GetType().GetElementType()!;
        var workCount = rowType.GetField("WorkCount")!;
        var ltqQuantity = rowType.GetField("LtqQuantity")!;
        var (work, ltq) = (0, 0);
        foreach (var row in rows)
        {
            work += (int)workCount.GetValue(row)! > 0 ? 1 : 0;
            ltq += (decimal)ltqQuantity.GetValue(row)! > 0 ? 1 : 0;
        }
        return (work, ltq);
    }

    // With the display tick aggregating, an MBO row merges the orders of the levels in its bucket.
    // Every frame of a random order feed still matches a full redraw
    [Theory]
    [MemberData(nameof(Paths))]
    public void AggregatedMboFeed_EveryFrameMatchesFullRedraw(LevelRemovalMode mode, bool retained)
    {
        const decimal displayTickSize = RandomFeed<OrderUpdate>.TickSize * 5;
        var feed = new RandomOrderFeed(seed: 5, ticksPerSecond: 150);
        var viewModel = new PriceLadderViewModel(RandomFeed<OrderUpdate>.TickSize) { DisplayTickSize = displayTickSize };
        var core = viewModel.Core;
        core.SetDataMode(DataMode.MBO);
        void Ingest((OrderUpdate Update, OrderUpdateType Type)[] batch)
        {
            foreach (var (update, type) in batch) core.ProcessOrderUpdateNoFlush(update, type);
            core.Flush();
        }
        Ingest(feed.Seed());
        using var ladder = new Ladder(mode, retained, displayTickSize: displayTickSize);
        using var fullRedraw = new Ladder(mode, retained: false, displayTickSize: displayTickSize);
        var mergedFrames = 0;

        for (var frame = 0; frame < 120; frame++)
        {
            foreach (var batch in feed.Advance((frame + 1) * 1000.0 / 60)) Ingest(batch);
            var snapshot = viewModel.CurrentSnapshot!.Value;
            if (ladder.Renderer.NeedsRender(snapshot, ladder.Viewport))
            {
                ladder.Render(snapshot);
            }
            fullRedraw.Renderer.Invalidate();
            fullRedraw.Render(snapshot);
            ladder.AssertMatches(fullRedraw, $"frame {frame}:");
            mergedFrames += snapshot.BidOrderBreaks?.Count > 0 && snapshot.AskOrderBreaks?.Count > 0 ? 1 : 0;
        }
        Assert.Equal(120, mergedFrames); // Rows of several levels on every frame
    }

    // A drag finds its limit from the last rendered book, so a level that outgrew the bar column
    // can be dragged to its end
    [Fact]
    public void HorizontalScroll_ReachesTheWidestLevelOfTheLastRender()
    {
        var book = new Book(DataMode.MBO).FlushOrders(OrderUpdateType.Add,
            new OrderUpdate(1, Side.BID, 100.00m, 100, 1), new OrderUpdate(2, Side.ASK, 100.01m, 100, 2));
        using var ladder = new Ladder(LevelRemovalMode.RemoveRow, retained: false);
        var state = SegmentState(ladder.Renderer);
        // Scrolled to the end of a level of n orders of 100
        double End(int n) => n * (100 * state.BasePixelsPerUnit * state.UserScaleFactor + RenderConfig.SegmentGapPx) -
            ladder.Viewport.OrdersColumnWidth;

        ladder.Render(book.Snapshot);
        ladder.Renderer.AdjustHorizontalScroll(1e6);
        Assert.Equal(End(1), state.HorizontalScrollOffset, precision: 6);

        // Orders no bigger than the largest: the scale stays, and the level outgrows the bar column
        var orders = new OrderUpdate[40];
        for (var i = 0; i < orders.Length; i++) orders[i] = new OrderUpdate(10 + i, Side.BID, 99.99m, 100, 10 + i);
        ladder.Render(book.FlushOrders(OrderUpdateType.Add, orders).Snapshot);
        ladder.Renderer.AdjustHorizontalScroll(1e6);
        Assert.Equal(End(40), state.HorizontalScrollOffset, precision: 6);
    }

    // Show Empty reads ascending levels only around the rows' prices. Off-grid prices just inside
    // and outside both edge rows must land where a full scan puts them (out-of-order input still
    // gets that scan)
    [Fact]
    public void ShowEmpty_DeepBook_PlacesTheSameLevelsAsAFullScan()
    {
        const decimal center = 100m, tick = 0.01m;
        var rows = new ViewportManager { Height = Height }.RowCount;
        var top = rows / 2; // Ticks from the center, top row
        var bottom = top - rows + 1;
        var ticks = new List<decimal> { top + 0.49m, top + 0.51m, bottom - 0.49m, bottom - 0.51m };
        for (var t = -500; t <= 500; t++)
        {
            if (t != top && t != bottom) ticks.Add(t); // The edge rows hold only their off-grid level
        }
        ticks.Sort();
        BookLevel[] Levels(Side side, int firstQuantity) =>
            ticks.Select((t, i) => new BookLevel(center + t * tick, firstQuantity + i, 1, side)).ToArray();
        var asks = Levels(Side.ASK, 1);
        var bids = Levels(Side.BID, 5000);

        byte[] Draw(BookLevel[] askLevels, BookLevel[] bidLevels)
        {
            using var renderer = new SkiaRenderer(new RenderConfig());
            using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
            var viewport = new ViewportManager
            {
                Width = Width, Height = Height, RemovalMode = LevelRemovalMode.ShowEmpty, AutoCenter = false, CenterPrice = center
            };
            var snapshot = new OrderBookSnapshot
            {
                BestBid = center, BestAsk = center + tick, MidPrice = center, Bids = bidLevels, Asks = askLevels, Timestamp = DateTime.UtcNow,
                Mode = DataMode.MBO // Level bars in the Orders column
            };
            renderer.Render(surface.Canvas, snapshot, viewport);
            return surface.PeekPixels().GetPixelSpan().ToArray();
        }

        var windowed = Draw(asks, bids);
        var scanned = Draw(asks.Reverse().ToArray(), bids.Reverse().ToArray());
        var index = windowed.AsSpan().CommonPrefixLength(scanned);
        Assert.True(index == windowed.Length, $"pixel ({index / 4 % Width}, {index / 4 / Width}) differs");
    }

    private static SegmentRenderState SegmentState(SkiaRenderer renderer) => (SegmentRenderState)typeof(SkiaRenderer)
        .GetField("_segmentState", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(renderer)!;

    [Fact]
    public void RenderRetained_RedrawsAfterInvalidate()
    {
        var book = Book.Seeded();
        using var ladder = new Ladder(LevelRemovalMode.RemoveRow, retained: true);
        ladder.Render(book.Snapshot);

        ladder.Surface.Canvas.Clear(SKColors.Magenta); // The host drew over the retained frame
        ladder.Renderer.Invalidate();
        ladder.Render(book.Snapshot);

        ladder.AssertMatchesFullRedraw(book.Snapshot);
    }

    [Fact]
    public void RenderRetained_NewSurfaceRedrawsEverything()
    {
        var book = Book.Seeded();
        using var ladder = new Ladder(LevelRemovalMode.ShowEmpty, retained: true);
        using var first = SKSurface.Create(new SKImageInfo(Width, Height));
        ladder.Renderer.RenderRetained(first, book.Snapshot, ladder.Viewport);

        ladder.Render(book.Snapshot); // Same book, but its own (blank) surface

        ladder.AssertMatchesFullRedraw(book.Snapshot);
    }

    [Fact]
    public void NeedsRender_OnlyWhenSnapshotOrViewChanges()
    {
        var book = Book.Seeded();
        using var ladder = new Ladder(LevelRemovalMode.RemoveRow, retained: true);
        var renderer = ladder.Renderer;
        var viewport = ladder.Viewport;

        void AssertRenderNeeded(string change)
        {
            Assert.True(renderer.NeedsRender(book.Snapshot, viewport), change);
            ladder.Render(book.Snapshot);
            Assert.False(renderer.NeedsRender(book.Snapshot, viewport), $"after rendering {change}");
        }

        AssertRenderNeeded("first frame");
        book.Flush(new PriceLevel(Side.ASK, 100.03m, 555, 3));
        AssertRenderNeeded("new snapshot");
        viewport.ShowOrderCount = true;
        AssertRenderNeeded("order count");
        viewport.AutoCenter = false;
        viewport.DensePackingScrollOffset = 3;
        AssertRenderNeeded("scroll");
        viewport.AutoCenter = true;
        AssertRenderNeeded("auto-center back on");
        renderer.AdjustHorizontalScroll(10);
        AssertRenderNeeded("bar scroll");
    }
}
