using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Subjects;
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
public class PriceLadderViewModel : ReactiveObject, IDisposable
{
    private readonly PriceLadderCore _core;
    private OrderBookSnapshot? _currentSnapshot;
    private decimal _displayTickSize;
    private decimal _tickSize;

    /// <summary>
    /// Event fired when user clicks a price level
    /// </summary>
    public event Action<LevelClickEventArgs>? OnLevelClick;

    // Every snapshot is new, so no equality check (RaiseAndSetIfChanged compares the struct by reflection)
    public OrderBookSnapshot? CurrentSnapshot
    {
        get => _currentSnapshot;
        private set
        {
            _currentSnapshot = value;
            this.RaisePropertyChanged();
        }
    }

    // Derived values are pushed from the snapshot handler only when they change. WhenAnyValue on
    // CurrentSnapshot would run ReactiveUI's change pipeline three times per flush, most of the
    // ingest cost with many ladders
    private readonly Subject<decimal> _spread = new();
    private readonly Subject<int> _bidLevelCount = new();
    private readonly Subject<int> _askLevelCount = new();
    private decimal _lastSpread;
    private int _lastBidLevelCount;
    private int _lastAskLevelCount;
    private int _disposed; // 1 once Dispose ran. A flush on the feed thread may be past the check

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
        _core.OnSnapshotReady += OnSnapshotReady;

        // Reactive properties derived from snapshot
        Spread = _spread.ToProperty(this, nameof(Spread));
        BidLevelCount = _bidLevelCount.ToProperty(this, nameof(BidLevelCount));
        AskLevelCount = _askLevelCount.ToProperty(this, nameof(AskLevelCount));
    }

    private void OnSnapshotReady(OrderBookSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return; // Flushed on another thread while Dispose unsubscribed
        }
        var current = _displayTickSize > _tickSize
            ? AggregateLevels(snapshot, _displayTickSize, _tickSize)
            : snapshot;
        CurrentSnapshot = current;
        PublishIfChanged(_spread, ref _lastSpread, _core.GetSpread() ?? 0m);
        PublishIfChanged(_bidLevelCount, ref _lastBidLevelCount, current.Bids.Length);
        PublishIfChanged(_askLevelCount, ref _lastAskLevelCount, current.Asks.Length);
    }

    /// <summary>
    /// Stop taking snapshots and complete Spread, BidLevelCount and AskLevelCount, so their
    /// subscribers are released. Safe while a feed thread flushes: the subjects are only completed,
    /// and a flush already in the snapshot handler can still publish to them. A completed subject
    /// drops the value, where a disposed one would throw
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _core.OnSnapshotReady -= OnSnapshotReady;
        _spread.OnCompleted();
        _bidLevelCount.OnCompleted();
        _askLevelCount.OnCompleted();
        Spread.Dispose();
        BidLevelCount.Dispose();
        AskLevelCount.Dispose();
    }

    private static void PublishIfChanged<T>(Subject<T> subject, ref T last, T value) where T : IEquatable<T>
    {
        if (last.Equals(value))
        {
            return;
        }
        last = value;
        subject.OnNext(value);
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

        var result = snapshot; // copy the struct
        result.BestBid = bestBid;
        result.BestAsk = bestAsk;
        result.MidPrice = midPrice;
        result.Bids = bids;
        result.Asks = asks;
        result.BidOrders = null;
        result.AskOrders = null;
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
