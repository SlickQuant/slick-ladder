using SlickLadder.Core.DataStructures;
using SlickLadder.Core.Models;
using System;
using System.Runtime.InteropServices;

namespace SlickLadder.Core.Managers;

/// <summary>
/// Market-By-Order (MBO) manager that tracks individual orders at each price level.
/// Maintains order-level granularity while aggregating to BookLevel for rendering.
/// </summary>
public class MBOManager : IMarketDataMode
{
    private readonly SortedArray<decimal, OrderLevel> _bidLevels;
    private readonly SortedArray<decimal, OrderLevel> _askLevels;
    private readonly OrderBook _orderBook;
    private readonly Dictionary<long, (decimal Price, Side Side)> _orderIndex;

    // Cached dictionaries for rendering (rebuilt only when dirty)
    private Dictionary<decimal, Order[]>? _cachedBidOrders;
    private Dictionary<decimal, Order[]>? _cachedAskOrders;
    private bool _bidOrdersDirty = true;
    private bool _askOrdersDirty = true;

    public MBOManager(OrderBook orderBook)
    {
        _orderBook = orderBook;
        _bidLevels = new SortedArray<decimal, OrderLevel>(200);
        _askLevels = new SortedArray<decimal, OrderLevel>(200);
        _orderIndex = new Dictionary<long, (decimal Price, Side Side)>(10000);
    }

    /// <summary>
    /// Process an order add operation.
    /// Creates new OrderLevel if price doesn't exist, adds order to level, updates aggregate.
    /// </summary>
    public void ProcessOrderAdd(OrderUpdate update)
    {
        var levels = update.Side == Side.BID ? _bidLevels : _askLevels;

        // Find or create OrderLevel at price
        OrderLevel? level;
        if (!levels.TryGetValue(update.Price, out level))
        {
            level = new OrderLevel(update.Price, update.Side);
            levels.AddOrUpdate(update.Price, level);
        }

        // Add order to level
        var order = new Order(update.OrderId, update.Quantity, update.Priority, update.IsOwnOrder);
        level.Orders.AddOrUpdate(update.OrderId, order);

        // Update cached aggregates
        level.TotalQuantity += update.Quantity;
        level.OrderCount++;
        level.IsDirty = true;
        level.MarkArrayDirty();

        // Mark dictionary cache dirty
        if (update.Side == Side.BID)
            _bidOrdersDirty = true;
        else
            _askOrdersDirty = true;

        // Index for fast lookup
        _orderIndex[update.OrderId] = (update.Price, update.Side);

        // Update OrderBook with aggregated BookLevel
        _orderBook.UpdateLevel(update.Price, level.TotalQuantity, level.OrderCount, update.Side);
    }

    /// <summary>
    /// Process an order modify operation.
    /// Updates quantity of existing order, recalculates aggregate.
    /// </summary>
    public void ProcessOrderModify(OrderUpdate update)
    {
        // Lookup existing order location
        if (!_orderIndex.TryGetValue(update.OrderId, out var location))
        {
            // Unknown order - ignore
            return;
        }

        var levels = location.Side == Side.BID ? _bidLevels : _askLevels;
        if (!levels.TryGetValue(location.Price, out var level))
        {
            // Level disappeared - should not happen, but handle gracefully
            _orderIndex.Remove(update.OrderId);
            return;
        }

        // Get existing order
        if (!level.Orders.TryGetValue(update.OrderId, out var existingOrder))
        {
            // Order disappeared - should not happen
            _orderIndex.Remove(update.OrderId);
            return;
        }

        // Update quantity delta
        var quantityDelta = update.Quantity - existingOrder.Quantity;
        level.TotalQuantity += quantityDelta;
        level.IsDirty = true;
        level.MarkArrayDirty();

        // Mark dictionary cache dirty
        if (location.Side == Side.BID)
            _bidOrdersDirty = true;
        else
            _askOrdersDirty = true;

        // Update order (preserve IsOwnOrder from existing order)
        var modifiedOrder = new Order(update.OrderId, update.Quantity, existingOrder.Priority, existingOrder.IsOwnOrder);
        level.Orders.AddOrUpdate(update.OrderId, modifiedOrder);

        // Update OrderBook
        _orderBook.UpdateLevel(location.Price, level.TotalQuantity, level.OrderCount, location.Side);
    }

    /// <summary>
    /// Process an order delete operation.
    /// Removes order from level, removes level if empty.
    /// </summary>
    public void ProcessOrderDelete(OrderUpdate update)
    {
        // Lookup and remove from index
        if (!_orderIndex.Remove(update.OrderId, out var location))
        {
            // Unknown order - ignore
            return;
        }

        var levels = location.Side == Side.BID ? _bidLevels : _askLevels;
        if (!levels.TryGetValue(location.Price, out var level))
        {
            // Level disappeared - should not happen
            return;
        }

        // Get existing order
        if (!level.Orders.TryGetValue(update.OrderId, out var existingOrder))
        {
            // Order disappeared
            return;
        }

        // Update cached aggregates
        level.TotalQuantity -= existingOrder.Quantity;
        level.OrderCount--;
        level.Orders.Remove(update.OrderId);
        level.IsDirty = true;
        level.MarkArrayDirty();

        // Mark dictionary cache dirty
        if (location.Side == Side.BID)
            _bidOrdersDirty = true;
        else
            _askOrdersDirty = true;

        // If level empty, remove from book
        if (level.OrderCount == 0)
        {
            levels.Remove(location.Price);
            _orderBook.UpdateLevel(location.Price, 0, 0, location.Side); // Qty=0 signals removal
        }
        else
        {
            _orderBook.UpdateLevel(location.Price, level.TotalQuantity, level.OrderCount, location.Side);
        }
    }

    /// <summary>
    /// Process OrderUpdate from binary data (IMarketDataMode interface).
    /// </summary>
    public void ProcessUpdate(ReadOnlySpan<byte> data)
    {
        // Parse OrderUpdate + type from binary
        // Format: [OrderUpdate:41 bytes][OrderUpdateType:1 byte] = 42 bytes
        if (data.Length < 42)
        {
            return; // Invalid data
        }

        var update = MemoryMarshal.Read<OrderUpdate>(data.Slice(0, 41));
        var type = (OrderUpdateType)data[41];

        ProcessOrderUpdate(update, type);
    }

    /// <summary>
    /// Process OrderUpdate with specified type.
    /// </summary>
    public void ProcessOrderUpdate(OrderUpdate update, OrderUpdateType type)
    {
        switch (type)
        {
            case OrderUpdateType.Add:
                ProcessOrderAdd(update);
                break;
            case OrderUpdateType.Modify:
                ProcessOrderModify(update);
                break;
            case OrderUpdateType.Delete:
                ProcessOrderDelete(update);
                break;
        }
    }

    /// <summary>
    /// Get OrderBook (aggregated levels).
    /// </summary>
    public OrderBook GetOrderBook() => _orderBook;

    /// <summary>
    /// Get individual orders for bid levels (for rendering).
    /// Uses cached dictionary if not dirty, rebuilds if dirty.
    /// </summary>
    public Dictionary<decimal, Order[]> GetBidOrders()
    {
        if (_bidOrdersDirty || _cachedBidOrders == null)
        {
            var prices = _bidLevels.Keys;
            var levels = _bidLevels.Values;
            var count = Math.Min(prices.Length, levels.Length);
            _cachedBidOrders = new Dictionary<decimal, Order[]>(count);
            for (int i = 0; i < count; i++)
            {
                _cachedBidOrders[prices[i]] = levels[i].GetOrdersArray();
            }
            _bidOrdersDirty = false;
        }
        return _cachedBidOrders;
    }

    /// <summary>
    /// Get individual orders for ask levels (for rendering).
    /// Uses cached dictionary if not dirty, rebuilds if dirty.
    /// </summary>
    public Dictionary<decimal, Order[]> GetAskOrders()
    {
        if (_askOrdersDirty || _cachedAskOrders == null)
        {
            var prices = _askLevels.Keys;
            var levels = _askLevels.Values;
            var count = Math.Min(prices.Length, levels.Length);
            _cachedAskOrders = new Dictionary<decimal, Order[]>(count);
            for (int i = 0; i < count; i++)
            {
                _cachedAskOrders[prices[i]] = levels[i].GetOrdersArray();
            }
            _askOrdersDirty = false;
        }
        return _cachedAskOrders;
    }

    /// <summary>
    /// Reset all state.
    /// </summary>
    public void Reset()
    {
        _bidLevels.Clear();
        _askLevels.Clear();
        _orderIndex.Clear();
        _orderBook.Clear();
        _cachedBidOrders = null;
        _cachedAskOrders = null;
        _bidOrdersDirty = true;
        _askOrdersDirty = true;
    }
}

/// <summary>
/// Represents all orders at a specific price level.
/// </summary>
public class OrderLevel
{
    public decimal Price { get; }
    public Side Side { get; }
    public SortedArray<long, Order> Orders { get; }
    public long TotalQuantity { get; set; }
    public int OrderCount { get; set; }
    public bool IsDirty { get; set; }

    // Cached order array for rendering (rebuilt only when dirty)
    private Order[]? _cachedOrderArray;
    private bool _arrayDirty = true;

    public OrderLevel(decimal price, Side side)
    {
        Price = price;
        Side = side;
        Orders = new SortedArray<long, Order>(64); // Average 50 orders per level
        TotalQuantity = 0;
        OrderCount = 0;
        IsDirty = true;
    }

    /// <summary>
    /// Mark the cached array as dirty (needs rebuild).
    /// Call this whenever Orders is modified.
    /// </summary>
    public void MarkArrayDirty()
    {
        _arrayDirty = true;
    }

    /// <summary>
    /// Get all orders as array (for rendering individual orders).
    /// Uses cached array if not dirty, rebuilds if dirty.
    /// </summary>
    public Order[] GetOrdersArray()
    {
        if (_arrayDirty || _cachedOrderArray == null)
        {
            var values = Orders.Values;
            if (values.Length == 0)
            {
                _cachedOrderArray = Array.Empty<Order>();
            }
            else
            {
                _cachedOrderArray = new Order[values.Length];
                values.CopyTo(_cachedOrderArray);
            }
            _arrayDirty = false;
        }
        return _cachedOrderArray;
    }
}

/// <summary>
/// Individual order representation.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Order
{
    public long OrderId;
    public long Quantity;
    public long Priority;
    public bool IsOwnOrder;

    public Order(long orderId, long quantity, long priority, bool isOwnOrder = false)
    {
        OrderId = orderId;
        Quantity = quantity;
        Priority = priority;
        IsOwnOrder = isOwnOrder;
    }
}
