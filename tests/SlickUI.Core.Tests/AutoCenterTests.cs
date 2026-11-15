using System;
using SkiaSharp;
using SlickLadder.Core;
using SlickLadder.Core.Models;
using SlickLadder.Rendering.Core;
using Xunit;

namespace SlickLadder.Core.Tests;

/// <summary>
/// Auto-center / manual re-center (ViewportManager.Recenter, SkiaRenderer auto-center)
/// </summary>
public class AutoCenterTests
{
    private static OrderBookSnapshot Snapshot(decimal? bestBid, decimal? bestAsk)
    {
        var bids = bestBid.HasValue ? new[] { new BookLevel(bestBid.Value, 100, 1, Side.BID) } : Array.Empty<BookLevel>();
        var asks = bestAsk.HasValue ? new[] { new BookLevel(bestAsk.Value, 100, 1, Side.ASK) } : Array.Empty<BookLevel>();
        return new OrderBookSnapshot
        {
            BestBid = bestBid,
            BestAsk = bestAsk,
            MidPrice = bestBid.HasValue && bestAsk.HasValue ? (bestBid + bestAsk) / 2 : null,
            Bids = bids,
            Asks = asks,
            Timestamp = DateTime.UtcNow
        };
    }

    [Fact]
    public void Recenter_MovesCenterToMidpoint_AndResetsRowScroll()
    {
        var viewport = new ViewportManager { CenterPrice = 50000m, DensePackingScrollOffset = 7 };

        viewport.Recenter(Snapshot(100.00m, 100.02m));

        Assert.Equal(100.01m, viewport.CenterPrice);
        Assert.Equal(0, viewport.DensePackingScrollOffset);
    }

    [Fact]
    public void Recenter_HalfTickMidpoint_RoundsAwayFromZeroLikeWeb()
    {
        // Mid 100.005 = 10000.5 ticks. Banker's rounding would give 100.00, web snapToDisplayTick gives 100.01
        var viewport = new ViewportManager();

        viewport.Recenter(Snapshot(100.00m, 100.01m));

        Assert.Equal(100.01m, viewport.CenterPrice);
    }

    [Theory]
    [InlineData(100.00, 100.08, 100.0)] // mid 100.04 -> 100.0
    [InlineData(100.00, 100.10, 100.1)] // mid 100.05 -> 100.1 (half away from zero)
    [InlineData(-100.10, -100.00, -100.1)] // mid -100.05 -> -100.1
    public void Recenter_SnapsToDisplayTickSize(double bid, double ask, double expected)
    {
        var viewport = new ViewportManager { TickSize = 0.01m, DisplayTickSize = 0.10m };

        viewport.Recenter(Snapshot((decimal)bid, (decimal)ask));

        Assert.Equal((decimal)expected, viewport.CenterPrice);
    }

    [Fact]
    public void Recenter_OneSidedBook_UsesAvailableBestPrice()
    {
        var viewport = new ViewportManager();

        viewport.Recenter(Snapshot(99.50m, null));
        Assert.Equal(99.50m, viewport.CenterPrice);

        viewport.Recenter(Snapshot(null, 101.25m));
        Assert.Equal(101.25m, viewport.CenterPrice);
    }

    [Fact]
    public void Recenter_EmptyBook_KeepsCenterPrice()
    {
        var viewport = new ViewportManager { CenterPrice = 123.45m, DensePackingScrollOffset = 3 };

        viewport.Recenter(Snapshot(null, null));

        Assert.Equal(123.45m, viewport.CenterPrice);
        Assert.Equal(0, viewport.DensePackingScrollOffset);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Render_AppliesAutoCenterOnlyWhenEnabled(bool autoCenter)
    {
        var viewport = new ViewportManager
        {
            Width = 400,
            Height = 600,
            AutoCenter = autoCenter,
            CenterPrice = 50000m,
            DensePackingScrollOffset = 5,
            RemovalMode = LevelRemovalMode.ShowEmpty
        };
        using var renderer = new SkiaRenderer(new RenderConfig());
        using var surface = SKSurface.Create(new SKImageInfo(viewport.Width, viewport.Height));

        renderer.Render(surface.Canvas, Snapshot(100.00m, 100.02m), viewport);

        Assert.Equal(autoCenter ? 100.01m : 50000m, viewport.CenterPrice);
        Assert.Equal(autoCenter ? 0 : 5, viewport.DensePackingScrollOffset);
    }
}
