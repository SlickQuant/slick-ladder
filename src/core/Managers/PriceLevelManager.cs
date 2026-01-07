using System.Runtime.InteropServices;
using SlickLadder.Core.Models;

namespace SlickLadder.Core.Managers;

/// <summary>
/// Manages aggregated price level updates.
/// Optimized for zero-copy binary parsing using MemoryMarshal.
/// </summary>
public class PriceLevelManager : IMarketDataMode
{
    private readonly OrderBook _orderBook;

    public PriceLevelManager(OrderBook orderBook)
    {
        _orderBook = orderBook;
    }

    /// <summary>
    /// Process a binary PriceLevel update using zero-copy parsing.
    /// Expected format: Side (1 byte) + Price (16 bytes) + Quantity (8 bytes) + NumOrders (4 bytes)
    /// Total: 29 bytes
    /// </summary>
    public void ProcessUpdate(ReadOnlySpan<byte> data)
    {
        if (data.Length < 29)
        {
            throw new ArgumentException("Invalid PriceLevel data length", nameof(data));
        }

        // Zero-copy parse using MemoryMarshal
        var priceLevel = MemoryMarshal.Read<PriceLevel>(data);

        // Update order book
        _orderBook.UpdateLevel(priceLevel);
    }

    /// <summary>
    /// Process a PriceLevel update directly (for testing or non-binary sources)
    /// </summary>
    public void ProcessUpdate(PriceLevel priceLevel)
    {
        _orderBook.UpdateLevel(priceLevel);
    }

    /// <summary>
    /// Process multiple price level updates in batch
    /// </summary>
    public void ProcessBatch(ReadOnlySpan<PriceLevel> updates)
    {
        foreach (var update in updates)
        {
            _orderBook.UpdateLevel(update);
        }
    }

    /// <summary>
    /// Process batch from binary data
    /// </summary>
    public void ProcessBatchBinary(ReadOnlySpan<byte> data)
    {
        const int updateSize = 29; // Size of PriceLevel struct
        var updateCount = data.Length / updateSize;

        for (int i = 0; i < updateCount; i++)
        {
            var updateData = data.Slice(i * updateSize, updateSize);
            ProcessUpdate(updateData);
        }
    }

    public OrderBook GetOrderBook() => _orderBook;

    public void Reset()
    {
        _orderBook.Clear();
    }
}
