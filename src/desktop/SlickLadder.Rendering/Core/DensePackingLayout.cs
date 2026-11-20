using SlickLadder.Core.Models;

namespace SlickLadder.Rendering.Core;

/// <summary>
/// Remove Rows layout: asks then bids on consecutive rows, scrolled by whole rows. SkiaRenderer
/// draws with it and ViewportManager.LevelPriceAt hit-tests with it, so clicks land on the rows
/// as drawn (mirrors buildDensePackingLayout in canvas-renderer.ts)
/// </summary>
internal readonly struct DensePackingLayout
{
    public readonly BookLevel[] NonEmptyAsks;
    public readonly BookLevel[] NonEmptyBids;
    public readonly int StartAskIndex;
    public readonly int AskRowsToRender;
    public readonly int StartBidIndex;
    public readonly int BidRowsToRender;
    public readonly float TopOffset;
    public readonly int FirstRowIndex;

    private DensePackingLayout(
        BookLevel[] nonEmptyAsks,
        BookLevel[] nonEmptyBids,
        int startAskIndex,
        int askRowsToRender,
        int startBidIndex,
        int bidRowsToRender,
        float topOffset,
        int firstRowIndex)
    {
        NonEmptyAsks = nonEmptyAsks;
        NonEmptyBids = nonEmptyBids;
        StartAskIndex = startAskIndex;
        AskRowsToRender = askRowsToRender;
        StartBidIndex = startBidIndex;
        BidRowsToRender = bidRowsToRender;
        TopOffset = topOffset;
        FirstRowIndex = firstRowIndex;
    }

    /// <summary>
    /// Lay out levels with quantity, each side ascending by price, on visibleRows rows
    /// </summary>
    public static DensePackingLayout Build(BookLevel[] nonEmptyAsks, BookLevel[] nonEmptyBids, int scrollOffset, int visibleRows, int midRow)
    {
        var totalLevels = nonEmptyAsks.Length + nonEmptyBids.Length;

        var virtualTopRow = nonEmptyAsks.Length - midRow + scrollOffset;

        int startAskIndex, askRowsToRender;
        int startBidIndex, bidRowsToRender;
        float topOffset = 0;

        if (virtualTopRow < 0)
        {
            topOffset = -virtualTopRow * RenderConfig.RowHeight;
            startAskIndex = 0;
            askRowsToRender = Math.Min(nonEmptyAsks.Length, Math.Max(0, visibleRows + virtualTopRow));
            startBidIndex = 0;
            var bidRowsAvailable = Math.Max(0, visibleRows + virtualTopRow - nonEmptyAsks.Length);
            bidRowsToRender = Math.Min(nonEmptyBids.Length, bidRowsAvailable);
        }
        else if (virtualTopRow < nonEmptyAsks.Length)
        {
            startAskIndex = virtualTopRow;
            askRowsToRender = Math.Min(nonEmptyAsks.Length - startAskIndex, visibleRows);
            startBidIndex = 0;
            var bidRowsAvailable = visibleRows - askRowsToRender;
            bidRowsToRender = Math.Min(nonEmptyBids.Length, bidRowsAvailable);
        }
        else if (virtualTopRow < totalLevels)
        {
            startAskIndex = nonEmptyAsks.Length;
            askRowsToRender = 0;
            var bidStartRow = virtualTopRow - nonEmptyAsks.Length;
            startBidIndex = bidStartRow;
            bidRowsToRender = Math.Min(nonEmptyBids.Length - startBidIndex, visibleRows);
        }
        else
        {
            startAskIndex = nonEmptyAsks.Length;
            askRowsToRender = 0;
            startBidIndex = nonEmptyBids.Length;
            bidRowsToRender = 0;
        }

        var firstRowIndex = (int)(topOffset / RenderConfig.RowHeight);

        return new DensePackingLayout(
            nonEmptyAsks,
            nonEmptyBids,
            startAskIndex,
            askRowsToRender,
            startBidIndex,
            bidRowsToRender,
            topOffset,
            firstRowIndex);
    }

    /// <summary>
    /// Level drawn on a row, null if none
    /// </summary>
    public BookLevel? LevelAt(int rowIndex)
    {
        var relativeRow = rowIndex - FirstRowIndex;
        if (relativeRow < 0)
        {
            return null;
        }
        if (relativeRow < AskRowsToRender)
        {
            var askIndex = NonEmptyAsks.Length - 1 - StartAskIndex - relativeRow;
            return askIndex >= 0 && askIndex < NonEmptyAsks.Length ? NonEmptyAsks[askIndex] : null;
        }
        var bidRow = relativeRow - AskRowsToRender;
        if (bidRow < BidRowsToRender)
        {
            var bidIndex = NonEmptyBids.Length - 1 - StartBidIndex - bidRow;
            return bidIndex >= 0 && bidIndex < NonEmptyBids.Length ? NonEmptyBids[bidIndex] : null;
        }
        return null;
    }

    /// <summary>
    /// Row of the ask level at an index of NonEmptyAsks, -1 if it isn't drawn
    /// </summary>
    public int AskRow(int askIndex)
    {
        var i = NonEmptyAsks.Length - 1 - StartAskIndex - askIndex;
        return i >= 0 && i < AskRowsToRender ? FirstRowIndex + i : -1;
    }

    /// <summary>
    /// Row of the bid level at an index of NonEmptyBids, -1 if it isn't drawn
    /// </summary>
    public int BidRow(int bidIndex)
    {
        var i = NonEmptyBids.Length - 1 - StartBidIndex - bidIndex;
        return i >= 0 && i < BidRowsToRender ? FirstRowIndex + AskRowsToRender + i : -1;
    }
}
