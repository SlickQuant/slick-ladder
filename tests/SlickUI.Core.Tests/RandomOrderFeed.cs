using System.Collections.Generic;
using SlickLadder.Core.Models;

namespace SlickLadder.Core.Tests;

/// <summary>
/// MBO version of RandomBookFeed (same rates and mid walk): each tick adds, modifies and deletes
/// orders, 1-5 per level to start. Bids and Asks hold each level's total quantity. Mirrors
/// OrderFeed in src/web/test-utils/random-feed.js
/// </summary>
internal sealed class RandomOrderFeed : RandomFeed<(OrderUpdate Update, OrderUpdateType Type)>
{
    private sealed class LiveOrder
    {
        public long Id;
        public Side Side;
        public long Tick;
        public decimal Quantity;
        public bool IsOwnOrder;
    }

    private readonly Dictionary<long, LiveOrder> _orders = new();
    private readonly List<long> _ids = new(); // Live order ids, to pick one at random
    private readonly Dictionary<long, HashSet<long>> _bidIds = new(); // Tick -> order ids
    private readonly Dictionary<long, HashSet<long>> _askIds = new();
    private long _nextId = 1;

    public RandomOrderFeed(int seed, double? ticksPerSecond = null, int depth = DefaultDepth)
        : base(seed, ticksPerSecond, depth)
    {
    }

    public override (OrderUpdate, OrderUpdateType)[] Seed()
    {
        var batch = new List<(OrderUpdate, OrderUpdateType)>();
        for (var distance = 1; distance <= Depth; distance++)
        {
            for (var n = Random.Next(1, 6); n > 0; n--)
            {
                batch.Add(Add(Side.BID, Mid - distance));
                batch.Add(Add(Side.ASK, Mid + distance));
            }
        }
        return batch.ToArray();
    }

    protected override (OrderUpdate, OrderUpdateType)[] Tick()
    {
        var batch = new List<(OrderUpdate, OrderUpdateType)>();
        if (Random.NextDouble() < 0.1)
        {
            // Keeps bids < mid < asks: delete the orders on the level the mid moves onto
            Mid += Random.NextDouble() < 0.5 ? -1 : 1;
            foreach (var ids in new[] { _bidIds, _askIds })
            {
                if (ids.TryGetValue(Mid, out var level))
                {
                    foreach (var id in new List<long>(level)) batch.Add(Remove(id));
                }
            }
        }
        var count = TickUpdateCount();
        for (var i = 0; i < count; i++)
        {
            var op = Random.NextDouble();
            if (_ids.Count < Depth * 2 || op < 0.35)
            {
                var side = Random.NextDouble() < 0.5 ? Side.BID : Side.ASK;
                var distance = Random.Next(1, Depth + 1);
                batch.Add(Add(side, side == Side.BID ? Mid - distance : Mid + distance));
            }
            else if (op < 0.65)
            {
                batch.Add(Modify(_ids[Random.Next(_ids.Count)]));
            }
            else
            {
                batch.Add(Remove(_ids[Random.Next(_ids.Count)]));
            }
        }
        return batch.ToArray();
    }

    private (OrderUpdate, OrderUpdateType) Add(Side side, long tick)
    {
        var order = new LiveOrder { Id = _nextId++, Side = side, Tick = tick, Quantity = Random.Next(1, 501), IsOwnOrder = Random.NextDouble() < 0.05 };
        _orders[order.Id] = order;
        _ids.Add(order.Id);
        var ids = side == Side.BID ? _bidIds : _askIds;
        if (!ids.TryGetValue(tick, out var level))
        {
            ids[tick] = level = new HashSet<long>();
        }
        level.Add(order.Id);
        AddTotal(order, order.Quantity);
        return Update(order, OrderUpdateType.Add);
    }

    private (OrderUpdate, OrderUpdateType) Modify(long id)
    {
        var order = _orders[id];
        var quantity = Random.Next(1, 501);
        AddTotal(order, quantity - order.Quantity);
        order.Quantity = quantity;
        return Update(order, OrderUpdateType.Modify);
    }

    private (OrderUpdate, OrderUpdateType) Remove(long id)
    {
        var order = _orders[id];
        _orders.Remove(id);
        var index = _ids.IndexOf(id);
        _ids[index] = _ids[^1];
        _ids.RemoveAt(_ids.Count - 1);
        var ids = order.Side == Side.BID ? _bidIds : _askIds;
        ids[order.Tick].Remove(id);
        if (ids[order.Tick].Count == 0) ids.Remove(order.Tick);
        AddTotal(order, -order.Quantity);
        return Update(order, OrderUpdateType.Delete);
    }

    private void AddTotal(LiveOrder order, decimal delta)
    {
        var book = order.Side == Side.BID ? Bids : Asks;
        var ids = order.Side == Side.BID ? _bidIds : _askIds;
        if (ids.ContainsKey(order.Tick))
        {
            book[order.Tick] = book.GetValueOrDefault(order.Tick) + delta;
        }
        else
        {
            book.Remove(order.Tick);
        }
    }

    private (OrderUpdate, OrderUpdateType) Update(LiveOrder order, OrderUpdateType type)
    {
        UpdateCount++;
        return (new OrderUpdate(order.Id, order.Side, order.Tick * TickSize, order.Quantity, order.Id, order.IsOwnOrder), type);
    }
}
