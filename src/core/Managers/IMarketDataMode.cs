using SlickLadder.Core.Models;

namespace SlickLadder.Core.Managers;

/// <summary>
/// Interface for different market data modes (PriceLevel vs MBO)
/// </summary>
public interface IMarketDataMode
{
    /// <summary>
    /// Process a raw market data update
    /// </summary>
    void ProcessUpdate(ReadOnlySpan<byte> data);

    /// <summary>
    /// Get current order book state
    /// </summary>
    OrderBook GetOrderBook();

    /// <summary>
    /// Reset the market data mode
    /// </summary>
    void Reset();
}
