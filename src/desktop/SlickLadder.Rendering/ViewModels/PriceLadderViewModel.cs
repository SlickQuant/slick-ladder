using System;
using System.Collections.Generic;
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

    public PriceLadderViewModel(decimal tickSize = 0.01m)
    {
        _core = new PriceLadderCore(maxLevels: 200, tickSize: tickSize);

        // Subscribe to snapshot updates from core
        _core.OnSnapshotReady += snapshot =>
        {
            CurrentSnapshot = snapshot;
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
}
