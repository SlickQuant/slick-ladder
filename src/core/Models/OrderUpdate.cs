using System.Runtime.InteropServices;

namespace SlickLadder.Core.Models;

/// <summary>
/// Market-By-Order (MBO) update.
/// Represents individual order-level updates.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct OrderUpdate
{
    public long OrderId;
    public Side Side;
    public decimal Price;
    public long Quantity;
    public long Priority;
    public bool IsOwnOrder;

    public OrderUpdate(long orderId, Side side, decimal price, long quantity, long priority, bool isOwnOrder = false)
    {
        OrderId = orderId;
        Side = side;
        Price = price;
        Quantity = quantity;
        Priority = priority;
        IsOwnOrder = isOwnOrder;
    }
}

/// <summary>
/// Type of order update operation
/// </summary>
public enum OrderUpdateType : byte
{
    Add = 0,
    Modify = 1,
    Delete = 2
}
