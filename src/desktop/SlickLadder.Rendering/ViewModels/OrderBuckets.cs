using SlickLadder.Core.Managers;

namespace SlickLadder.Rendering.ViewModels;

/// <summary>
/// MBO orders merged into display buckets (mirrors OrderBuckets in aggregation.ts): each bucket
/// shows the orders of every price level in it, the level nearest the spread first (bids from the
/// highest price, asks from the lowest), each in its usual order. Breaks holds where each level
/// after the first starts in a bucket's orders (buckets of one level have none). A bucket whose
/// levels' order arrays haven't changed keeps its merged array, and both maps stay the same while
/// no bucket changed, so the renderer tells by reference. Lock-free: each merge reads one state and
/// publishes a new one whole, so merges on two threads each return a matching pair
/// </summary>
internal sealed class OrderBuckets
{
    private sealed record Bucket(Order[][] Sources, Order[] Orders, int[] Breaks);

    private sealed record State(
        decimal DisplayTickSize,
        Dictionary<decimal, Bucket> Buckets,
        Dictionary<decimal, Order[]> Orders,
        Dictionary<decimal, int[]> Breaks);

    private readonly bool _roundUp; // Asks, which bucket up like their levels
    private State? _state;

    public OrderBuckets(bool roundUp) => _roundUp = roundUp;

    public (Dictionary<decimal, Order[]> Orders, Dictionary<decimal, int[]> Breaks) Merge(
        Dictionary<decimal, Order[]> orderMap, decimal displayTickSize)
    {
        var previous = Volatile.Read(ref _state);
        if (previous != null && previous.DisplayTickSize != displayTickSize)
        {
            previous = null;
        }

        var pricesByBucket = new Dictionary<decimal, List<decimal>>();
        foreach (var price in orderMap.Keys)
        {
            var bucketPrice = PriceLadderViewModel.BucketPrice(price, displayTickSize, _roundUp);
            if (!pricesByBucket.TryGetValue(bucketPrice, out var prices))
            {
                pricesByBucket[bucketPrice] = prices = new List<decimal>(2);
            }
            prices.Add(price);
        }

        var buckets = new Dictionary<decimal, Bucket>(pricesByBucket.Count);
        var changed = previous == null || pricesByBucket.Count != previous.Buckets.Count;
        foreach (var (bucketPrice, prices) in pricesByBucket)
        {
            prices.Sort();
            if (!_roundUp)
            {
                prices.Reverse(); // Nearest the spread first
            }
            var sources = new Order[prices.Count][];
            for (var i = 0; i < sources.Length; i++)
            {
                sources[i] = orderMap[prices[i]];
            }
            Bucket? old = null;
            previous?.Buckets.TryGetValue(bucketPrice, out old);
            var bucket = old != null && SameSources(old.Sources, sources) ? old : MergeBucket(sources);
            changed |= !ReferenceEquals(bucket, old);
            buckets[bucketPrice] = bucket;
        }

        if (!changed)
        {
            return (previous!.Orders, previous.Breaks);
        }

        var orders = new Dictionary<decimal, Order[]>(buckets.Count);
        var breaks = new Dictionary<decimal, int[]>();
        foreach (var (bucketPrice, bucket) in buckets)
        {
            orders[bucketPrice] = bucket.Orders;
            if (bucket.Breaks.Length > 0)
            {
                breaks[bucketPrice] = bucket.Breaks;
            }
        }
        Volatile.Write(ref _state, new State(displayTickSize, buckets, orders, breaks));
        return (orders, breaks);
    }

    private static Bucket MergeBucket(Order[][] sources)
    {
        if (sources.Length == 1)
        {
            return new Bucket(sources, sources[0], Array.Empty<int>()); // One level: its own array
        }
        var orders = new Order[sources.Sum(levelOrders => levelOrders.Length)];
        var breaks = new int[sources.Length - 1];
        var count = 0;
        for (var i = 0; i < sources.Length; i++)
        {
            if (i > 0)
            {
                breaks[i - 1] = count;
            }
            sources[i].CopyTo(orders, count);
            count += sources[i].Length;
        }
        return new Bucket(sources, orders, breaks);
    }

    private static bool SameSources(Order[][] a, Order[][] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        for (var i = 0; i < a.Length; i++)
        {
            if (!ReferenceEquals(a[i], b[i]))
            {
                return false;
            }
        }
        return true;
    }
}
