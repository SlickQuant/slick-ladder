using System.Collections;
using SlickLadder.Core.DataStructures;
using SlickLadder.Core.Models;

namespace SlickLadder.Core;

/// <summary>
/// Core order book implementation.
/// Maintains sorted bid and ask price levels using cache-friendly SortedArray.
/// Optimized for 100-200 price levels with <1ms update latency.
/// </summary>
public class OrderBook
{
    private readonly SortedArray<decimal, BookLevel> _bids;
    private readonly SortedArray<decimal, BookLevel> _asks;
    private readonly BitArray _dirtyLevels;
    private readonly int _maxLevels;
    private readonly decimal _tickSize;

    /// <summary>Number of bid levels</summary>
    public int BidCount => _bids.Count;

    /// <summary>Number of ask levels</summary>
    public int AskCount => _asks.Count;

    /// <summary>Best bid price (highest)</summary>
    public decimal? BestBid => _bids.Count > 0 ? _bids.GetKeyByIndex(_bids.Count - 1) : null;

    /// <summary>Best ask price (lowest)</summary>
    public decimal? BestAsk => _asks.Count > 0 ? _asks.GetKeyByIndex(0) : null;

    /// <summary>Mid price (average of best bid and ask)</summary>
    public decimal? MidPrice
    {
        get
        {
            if (BestBid.HasValue && BestAsk.HasValue)
                return (BestBid.Value + BestAsk.Value) / 2;
            return null;
        }
    }

    /// <summary>Tick size for price increments</summary>
    public decimal TickSize => _tickSize;

    public OrderBook(int maxLevels = 200, decimal tickSize = 0.01m)
    {
        _maxLevels = maxLevels;
        _tickSize = tickSize;
        _bids = new SortedArray<decimal, BookLevel>(maxLevels);
        _asks = new SortedArray<decimal, BookLevel>(maxLevels);
        _dirtyLevels = new BitArray(maxLevels * 2); // Bids + Asks
    }

    /// <summary>
    /// Update or add a price level. O(log n) lookup + O(1) or O(n) insert.
    /// Marks the level as dirty for incremental rendering.
    /// </summary>
    public void UpdateLevel(decimal price, long quantity, int numOrders, Side side)
    {
        var level = new BookLevel(price, quantity, numOrders, side);
        level.IsDirty = true;

        if (side == Side.BID)
        {
            if (quantity > 0)
            {
                _bids.AddOrUpdate(price, level);
            }
            else
            {
                // Quantity 0 means remove level
                _bids.Remove(price);
            }
        }
        else // ASK
        {
            if (quantity > 0)
            {
                _asks.AddOrUpdate(price, level);
            }
            else
            {
                _asks.Remove(price);
            }
        }

        MarkDirty(price, side);
    }

    /// <summary>
    /// Update a price level from PriceLevel struct
    /// </summary>
    public void UpdateLevel(PriceLevel priceLevel)
    {
        UpdateLevel(priceLevel.Price, priceLevel.Quantity, priceLevel.NumOrders, priceLevel.Side);
    }

    /// <summary>
    /// Get a specific price level
    /// </summary>
    public bool TryGetLevel(decimal price, Side side, out BookLevel level)
    {
        if (side == Side.BID)
        {
            return _bids.TryGetValue(price, out level);
        }
        else
        {
            return _asks.TryGetValue(price, out level);
        }
    }

    /// <summary>
    /// Mark a price level as having user's own orders
    /// </summary>
    public void MarkOwnOrder(decimal price, Side side, bool hasOwnOrder)
    {
        if (TryGetLevel(price, side, out var level))
        {
            level.HasOwnOrders = hasOwnOrder;
            level.IsDirty = true;

            if (side == Side.BID)
            {
                _bids.AddOrUpdate(price, level);
            }
            else
            {
                _asks.AddOrUpdate(price, level);
            }

            MarkDirty(price, side);
        }
    }

    /// <summary>
    /// Get a range of bid levels (highest to lowest)
    /// </summary>
    /// <param name="count">Maximum number of levels to return</param>
    public ReadOnlySpan<BookLevel> GetTopBids(int count)
    {
        if (_bids.Count == 0) return ReadOnlySpan<BookLevel>.Empty;

        // Bids are sorted low to high, we want highest first
        var startIndex = Math.Max(0, _bids.Count - count);
        var actualCount = Math.Min(count, _bids.Count);

        return _bids.GetRange(startIndex, actualCount);
    }

    /// <summary>
    /// Get a range of ask levels (lowest to highest)
    /// </summary>
    /// <param name="count">Maximum number of levels to return</param>
    public ReadOnlySpan<BookLevel> GetTopAsks(int count)
    {
        if (_asks.Count == 0) return ReadOnlySpan<BookLevel>.Empty;

        var actualCount = Math.Min(count, _asks.Count);
        return _asks.GetRange(0, actualCount);
    }

    /// <summary>
    /// Get bids within a price range
    /// </summary>
    public ReadOnlySpan<BookLevel> GetBidsInRange(decimal minPrice, decimal maxPrice)
    {
        if (_bids.Count == 0) return ReadOnlySpan<BookLevel>.Empty;

        var startIdx = _bids.LowerBound(minPrice);
        var endIdx = _bids.UpperBound(maxPrice);

        if (startIdx >= _bids.Count || endIdx <= 0)
            return ReadOnlySpan<BookLevel>.Empty;

        var count = endIdx - startIdx;
        return _bids.GetRange(startIdx, count);
    }

    /// <summary>
    /// Get asks within a price range
    /// </summary>
    public ReadOnlySpan<BookLevel> GetAsksInRange(decimal minPrice, decimal maxPrice)
    {
        if (_asks.Count == 0) return ReadOnlySpan<BookLevel>.Empty;

        var startIdx = _asks.LowerBound(minPrice);
        var endIdx = _asks.UpperBound(maxPrice);

        if (startIdx >= _asks.Count || endIdx <= 0)
            return ReadOnlySpan<BookLevel>.Empty;

        var count = endIdx - startIdx;
        return _asks.GetRange(startIdx, count);
    }

    /// <summary>
    /// Clear all dirty flags (call after rendering)
    /// </summary>
    public void ClearDirtyFlags()
    {
        _dirtyLevels.SetAll(false);

        // Clear dirty flags on all levels
        ClearDirtyFlagsInternal(_bids);
        ClearDirtyFlagsInternal(_asks);
    }

    private void ClearDirtyFlagsInternal(SortedArray<decimal, BookLevel> levels)
    {
        for (int i = 0; i < levels.Count; i++)
        {
            var level = levels.GetByIndex(i);
            if (level.IsDirty)
            {
                level.IsDirty = false;
                levels.AddOrUpdate(level.Price, level);
            }
        }
    }

    /// <summary>
    /// Get all dirty levels for incremental rendering
    /// </summary>
    public List<BookLevel> GetDirtyLevels()
    {
        var dirtyLevels = new List<BookLevel>();

        // Check dirty bids
        for (int i = 0; i < _bids.Count; i++)
        {
            var level = _bids.GetByIndex(i);
            if (level.IsDirty)
            {
                dirtyLevels.Add(level);
            }
        }

        // Check dirty asks
        for (int i = 0; i < _asks.Count; i++)
        {
            var level = _asks.GetByIndex(i);
            if (level.IsDirty)
            {
                dirtyLevels.Add(level);
            }
        }

        return dirtyLevels;
    }

    /// <summary>
    /// Clear the entire order book
    /// </summary>
    public void Clear()
    {
        _bids.Clear();
        _asks.Clear();
        _dirtyLevels.SetAll(false);
    }

    /// <summary>
    /// Mark a price level as dirty for rendering
    /// </summary>
    private void MarkDirty(decimal price, Side side)
    {
        // Simple dirty tracking - could be optimized with price-to-index mapping
        // For now, we track dirty state in the BookLevel itself
    }

    /// <summary>
    /// Get snapshot of visible levels around a center price
    /// </summary>
    public OrderBookSnapshot GetSnapshot(decimal centerPrice, int visibleLevels, bool fillEmptyLevels = false)
    {
        var halfLevels = visibleLevels / 2;

        // Get bids at or below center price (inclusive)
        var bidMinPrice = centerPrice - (halfLevels * _tickSize);
        var bids = GetBidsInRange(bidMinPrice, centerPrice);

        // Get asks above center price (exclusive - start from center + 1 tick)
        var askMinPrice = centerPrice + _tickSize;
        var askMaxPrice = centerPrice + (halfLevels * _tickSize);
        var asks = GetAsksInRange(askMinPrice, askMaxPrice);

        // Fill in empty levels if requested (for "Show Empty" mode)
        if (fillEmptyLevels)
        {
            bids = FillEmptyLevels(bids, bidMinPrice, centerPrice, _tickSize, Side.BID).ToArray();
            asks = FillEmptyLevels(asks, askMinPrice, askMaxPrice, _tickSize, Side.ASK).ToArray();
        }

        return new OrderBookSnapshot
        {
            BestBid = BestBid,
            BestAsk = BestAsk,
            MidPrice = MidPrice,
            Bids = bids.ToArray(),
            Asks = asks.ToArray(),
            Timestamp = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Fill in missing price levels with empty levels (qty=0)
    /// Creates a continuous ladder with no gaps
    /// </summary>
    private ReadOnlySpan<BookLevel> FillEmptyLevels(ReadOnlySpan<BookLevel> levels, decimal minPrice, decimal maxPrice, decimal tickSize, Side side)
    {
        var result = new List<BookLevel>();
        var currentPrice = minPrice;

        // Convert span to array for easier lookup
        var levelDict = new Dictionary<decimal, BookLevel>();
        foreach (var level in levels)
        {
            levelDict[level.Price] = level;
        }

        // Calculate decimal places for rounding based on tick size
        int decimalPlaces = GetDecimalPlaces(tickSize);

        // Fill all prices from min to max with tick increments
        while (currentPrice <= maxPrice)
        {
            if (levelDict.TryGetValue(currentPrice, out var existingLevel))
            {
                result.Add(existingLevel);
            }
            else
            {
                // Create empty level
                result.Add(new BookLevel(currentPrice, 0, 0, side));
            }
            currentPrice += tickSize;
            currentPrice = Math.Round(currentPrice, decimalPlaces); // Avoid floating point errors
        }

        return result.ToArray();
    }

    /// <summary>
    /// Calculate the number of decimal places needed for a given tick size
    /// </summary>
    private static int GetDecimalPlaces(decimal tickSize)
    {
        int decimalPlaces = 0;
        decimal test = tickSize;

        // Multiply by 10 until we get a value >= 1
        while (test < 1 && decimalPlaces < 10)
        {
            test *= 10;
            decimalPlaces++;
        }

        // Verify if this number of decimals is sufficient
        decimal multiplier = (decimal)Math.Pow(10, decimalPlaces);
        decimal rounded = Math.Round(tickSize * multiplier);

        // If not a whole number, we may need more decimals
        while (Math.Abs((rounded / multiplier) - tickSize) > 0.0000000001m && decimalPlaces < 10)
        {
            decimalPlaces++;
            multiplier = (decimal)Math.Pow(10, decimalPlaces);
            rounded = Math.Round(tickSize * multiplier);
        }

        return decimalPlaces;
    }
}

/// <summary>
/// Snapshot of order book state at a point in time
/// </summary>
public struct OrderBookSnapshot
{
    public decimal? BestBid;
    public decimal? BestAsk;
    public decimal? MidPrice;
    public BookLevel[] Bids;
    public BookLevel[] Asks;
    public DateTime Timestamp;
}
