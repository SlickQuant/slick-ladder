using System.Runtime.InteropServices;

namespace SlickLadder.Core.Models;

/// <summary>
/// Represents a single price level in the order book.
/// Value type (struct) for zero-allocation performance.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct BookLevel
{
    /// <summary>Price of this level</summary>
    public decimal Price;

    /// <summary>Total quantity at this price level</summary>
    public long Quantity;

    /// <summary>Number of orders at this price level</summary>
    public int NumOrders;

    /// <summary>Side of the book (BID or ASK)</summary>
    public Side Side;

    /// <summary>Flags: bit 0 = IsDirty, bit 1 = HasOwnOrders</summary>
    public byte Flags;

    /// <summary>Whether this level has changed since last render</summary>
    public bool IsDirty
    {
        get => (Flags & 0x01) != 0;
        set => Flags = value ? (byte)(Flags | 0x01) : (byte)(Flags & ~0x01);
    }

    /// <summary>Whether user has orders at this price level</summary>
    public bool HasOwnOrders
    {
        get => (Flags & 0x02) != 0;
        set => Flags = value ? (byte)(Flags | 0x02) : (byte)(Flags & ~0x02);
    }

    public BookLevel(decimal price, long quantity, int numOrders, Side side)
    {
        Price = price;
        Quantity = quantity;
        NumOrders = numOrders;
        Side = side;
        Flags = 0x01; // Mark as dirty initially
    }
}
