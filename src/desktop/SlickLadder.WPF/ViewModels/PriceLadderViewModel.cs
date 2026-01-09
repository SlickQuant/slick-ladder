using ReactiveUI;
using SlickLadder.Core;
using SlickLadder.Core.Models;
using System.Reactive.Linq;

namespace SlickLadder.WPF.ViewModels;

/// <summary>
/// ReactiveUI ViewModel for the WPF PriceLadder control.
/// Provides MVVM binding to the core business logic.
/// </summary>
public class PriceLadderViewModel : ReactiveObject
{
    private readonly PriceLadderCore _core;
    private OrderBookSnapshot? _currentSnapshot;

    public OrderBookSnapshot? CurrentSnapshot
    {
        get => _currentSnapshot;
        private set => this.RaiseAndSetIfChanged(ref _currentSnapshot, value);
    }

    // Observable properties
    public ObservableAsPropertyHelper<decimal> Spread { get; }
    public ObservableAsPropertyHelper<int> BidLevelCount { get; }
    public ObservableAsPropertyHelper<int> AskLevelCount { get; }

    public PriceLadderViewModel()
    {
        _core = new PriceLadderCore(maxLevels: 200);

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
    /// Handle price level click (for click-to-trade)
    /// </summary>
    public void HandlePriceClick(decimal price, Side side)
    {
        // Mark as own order for visual feedback
        _core.MarkOwnOrder(price, side);
    }

    /// <summary>
    /// Get access to the core for advanced scenarios
    /// </summary>
    public PriceLadderCore Core => _core;
}
