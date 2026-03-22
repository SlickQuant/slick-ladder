using System.Runtime.InteropServices;

namespace SlickLadder.Core.Models;

/// <summary>
/// Aggregated price level update (for PriceLevel mode).
/// Used for parsing incoming market data.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PriceLevel
{
    public Side Side;
    public decimal Price;
    public decimal Quantity;
    public int NumOrders;

    public PriceLevel(Side side, decimal price, decimal quantity, int numOrders)
    {
        Side = side;
        Price = price;
        Quantity = quantity;
        NumOrders = numOrders;
    }
}
