using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using ReactiveUI;
using SlickLadder.Core;
using SlickLadder.Core.Models;

namespace SlickLadder.Rendering.ViewModels;

/// <summary>
/// Event args for a price level click
/// </summary>
public class LevelClickEventArgs
{
    public decimal? Price { get; }
    public Side Side { get; }

    public LevelClickEventArgs(decimal? price, Side side)
    {
        Price = price;
        Side = side;
    }
}

/// <summary>
/// Shared ReactiveUI ViewModel for both WPF and Avalonia PriceLadder controls.
/// Provides MVVM binding to the core business logic.
/// </summary>
public class PriceLadderViewModel : ReactiveObject
{
    private readonly PriceLadderCore _core;
    private OrderBookSnapshot? _currentSnapshot;
    private decimal _displayTickSize;
    private decimal _tickSize;

    /// <summary>
    /// Event fired when user clicks a price level
    /// </summary>
    public event Action<LevelClickEventArgs>? OnLevelClick;

    public OrderBookSnapshot? CurrentSnapshot
    {
        get => _currentSnapshot;
        private set => this.RaiseAndSetIfChanged(ref _currentSnapshot, value);
    }

    // Observable properties
    public ObservableAsPropertyHelper<decimal> Spread { get; }
    public ObservableAsPropertyHelper<int> BidLevelCount { get; }
    public ObservableAsPropertyHelper<int> AskLevelCount { get; }

    /// <summary>
    /// Display bucket size for aggregation. Must be >= TickSize.
    /// Setting this to a value greater than TickSize aggregates multiple raw levels into one display row.
    /// </summary>
    public decimal DisplayTickSize
    {
        get => _displayTickSize;
        set
        {
            var clamped = value >= _tickSize ? value : _tickSize;
            this.RaiseAndSetIfChanged(ref _displayTickSize, clamped);
        }
    }

    public PriceLadderViewModel(decimal tickSize = 0.01m)
    {
        _tickSize = tickSize;
        _displayTickSize = tickSize;
        _core = new PriceLadderCore(maxLevels: 200, tickSize: tickSize);

        // Subscribe to snapshot updates from core
        _core.OnSnapshotReady += snapshot =>
        {
            CurrentSnapshot = _displayTickSize > _tickSize
                ? AggregateLevels(snapshot, _displayTickSize, _tickSize)
                : snapshot;
        };

        // Reactive properties derived from snapshot
        Spread = this.WhenAnyValue(x => x.CurrentSnapshot)
            .Select(_ => _core.GetSpread() ?? 0m)
            .ToProperty(this, nameof(Spread));

        BidLevelCount = this.WhenAnyValue(x => x.CurrentSnapshot)
            .Select(s => s?.Bids.Length ?? 0)
            .ToProperty(this, nameof(BidLevelCount));

        AskLevelCount = this.WhenAnyValue(x => x.CurrentSnapshot)
            .Select(s => s?.Asks.Length ?? 0)
            .ToProperty(this, nameof(AskLevelCount));
    }

    /// <summary>
    /// Process a single price level update
    /// </summary>
    public void ProcessUpdate(PriceLevel update)
    {
        _core.ProcessPriceLevelUpdate(update);
    }

    /// <summary>
    /// Process a batch of updates (more efficient)
    /// </summary>
    public void ProcessBatch(IEnumerable<PriceLevel> updates)
    {
        foreach (var update in updates)
        {
            _core.ProcessPriceLevelUpdateNoFlush(update);
        }
        _core.Flush();
    }

    /// <summary>
    /// Handle price level click
    /// </summary>
    public void HandlePriceClick(decimal? price, Side side)
    {
        OnLevelClick?.Invoke(new LevelClickEventArgs(price, side));
    }

    /// <summary>
    /// Get access to the core for advanced scenarios
    /// </summary>
    public PriceLadderCore Core => _core;

    private static decimal BucketPrice(decimal price, decimal displayTickSize, bool roundUp = false)
    {
        var decimalPlaces = GetDecimalPlaces(displayTickSize);
        var raw = (roundUp ? Math.Ceiling(price / displayTickSize) : Math.Floor(price / displayTickSize)) * displayTickSize;
        return Math.Round(raw, decimalPlaces);
    }

    private static int GetDecimalPlaces(decimal value)
    {
        if (value >= 1m) return 0;
        var str = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var dotIndex = str.IndexOf('.');
        return dotIndex == -1 ? 0 : str.Length - dotIndex - 1;
    }

    private static OrderBookSnapshot AggregateLevels(OrderBookSnapshot snapshot, decimal displayTickSize, decimal tickSize)
    {
        var bids = AggregateArray(snapshot.Bids, displayTickSize, Side.BID);
        var asks = AggregateArray(snapshot.Asks, displayTickSize, Side.ASK);

        var bestBid = bids.Length > 0 ? bids[bids.Length - 1].Price : (decimal?)null;
        var bestAsk = asks.Length > 0 ? asks[0].Price : (decimal?)null;
        var midPrice = bestBid.HasValue && bestAsk.HasValue
            ? (bestBid.Value + bestAsk.Value) / 2
            : (decimal?)null;

        DirtyLevelChange[]? dirty = null;
        if (snapshot.DirtyChanges != null)
        {
            var seen = new HashSet<string>();
            var remapped = new List<DirtyLevelChange>();
            foreach (var c in snapshot.DirtyChanges)
            {
                var bp = BucketPrice(c.Price, displayTickSize, c.Side == Side.ASK);
                var key = $"{(int)c.Side}_{bp}";
                if (seen.Add(key))
                    remapped.Add(new DirtyLevelChange(bp, c.Side, c.IsRemoval, c.IsAddition));
            }
            dirty = remapped.ToArray();
        }

        var result = snapshot; // copy the struct
        result.BestBid = bestBid;
        result.BestAsk = bestAsk;
        result.MidPrice = midPrice;
        result.Bids = bids;
        result.Asks = asks;
        result.BidOrders = null;
        result.AskOrders = null;
        result.DirtyChanges = dirty;
        return result;
    }

    private static BookLevel[] AggregateArray(BookLevel[] levels, decimal displayTickSize, Side side)
    {
        var roundUp = side == Side.ASK;
        var buckets = new Dictionary<decimal, BookLevel>();
        foreach (var level in levels)
        {
            var bp = BucketPrice(level.Price, displayTickSize, roundUp);
            if (buckets.TryGetValue(bp, out var existing))
            {
                var merged = new BookLevel(bp, existing.Quantity + level.Quantity, existing.NumOrders + level.NumOrders, side);
                merged.IsDirty = existing.IsDirty || level.IsDirty;
                merged.HasOwnOrders = existing.HasOwnOrders || level.HasOwnOrders;
                buckets[bp] = merged;
            }
            else
            {
                var bucket = new BookLevel(bp, level.Quantity, level.NumOrders, side);
                bucket.IsDirty = level.IsDirty;
                bucket.HasOwnOrders = level.HasOwnOrders;
                buckets[bp] = bucket;
            }
        }
        return buckets.Values.OrderBy(l => l.Price).ToArray();
    }
}
